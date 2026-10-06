using System.Text.Json;
using System.Text.Json.Nodes;
using Casco.Api.Features.Projects;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Casco.Api.Features.Backend;

/// <summary>Custom collections declared in casco.backend.json: public site API + owner dashboard API.</summary>
public static class CollectionEndpoints
{
    public static void MapCollectionEndpoints(this IEndpointRouteBuilder app)
    {
        var s = app.MapGroup("/api/s/{siteKey}/db/{collection}").RequireCors("sites").RequireRateLimiting("site");

        s.MapGet("", async (string siteKey, string collection, string? where, string? sort, string? q, int? page, int? pageSize,
            HttpContext http, SiteContext site, RecordStore store) =>
        {
            var (project, def) = await ResolveAsync(site, siteKey, collection, http);
            var viewer = await CanReadAsync(site, http, project, def);
            var rows = await store.LoadAsync(site.Db, project.Id, collection);
            if (def.Access.Read == Access.Owner) rows = rows.Where(r => r.UserId == viewer).ToList();
            var (items, total) = RecordStore.Query(rows, def, new RecordQuery(ParseWhere(where), sort, q, page ?? 1, pageSize ?? 20), admin: false);
            return Results.Ok(new { items = items.Select(r => RecordStore.Shape(def, r, viewer, false)), total, page = Math.Max(1, page ?? 1) });
        });

        s.MapGet("/{rid:guid}", async (string siteKey, string collection, Guid rid, HttpContext http, SiteContext site, RecordStore store) =>
        {
            var (project, def) = await ResolveAsync(site, siteKey, collection, http);
            var viewer = await CanReadAsync(site, http, project, def);
            var rows = await store.LoadAsync(site.Db, project.Id, collection);
            var row = rows.FirstOrDefault(r => r.Id == rid);
            if (row is null || (def.Access.Read == Access.Owner && row.UserId != viewer)
                || RecordStore.Query([row], def, new RecordQuery(), admin: false).Total == 0)
                throw ApiException.NotFound("العنصر غير موجود");
            return Results.Ok(RecordStore.Shape(def, row, viewer, false));
        });

        s.MapPost("", async (string siteKey, string collection, JsonObject body, HttpContext http, SiteContext site, RecordStore store, UploadService uploads) =>
        {
            var (project, def) = await ResolveAsync(site, siteKey, collection, http);
            if (body["_hp"] is JsonValue hp && hp.ToString().Length > 0) return Results.Ok(new { ok = true });
            Guid? userId = def.Access.Create switch
            {
                Access.Public => await site.SiteUserIdAsync(http, project),
                Access.Users => (await site.RequireUserAsync(http, project)).Id,
                _ => throw ApiException.Forbidden("الإضافة هنا لصاحب الموقع فقط")
            };
            await site.EnsureTrialCapacityAsync(project, () =>
                site.Db.SiteRecords.CountAsync(r => r.ProjectId == project.Id && r.Collection == collection));
            var data = BackendConfig.ValidateData(def, body, partial: false, admin: false, uploads.PublicPrefix(project.Id));
            var row = await store.CreateAsync(site.Db, project.Id, collection, data, userId);
            return Results.Ok(RecordStore.Shape(def, row, userId, false));
        }).RequireRateLimiting("forms");

        s.MapPatch("/{rid:guid}", async (string siteKey, string collection, Guid rid, JsonObject body, HttpContext http,
            SiteContext site, RecordStore store, UploadService uploads) =>
        {
            var (project, def) = await ResolveAsync(site, siteKey, collection, http);
            var user = await RequireOwnerOfAsync(site, http, project, def, def.Access.Update, rid);
            var patch = BackendConfig.ValidateData(def, body, partial: true, admin: false, uploads.PublicPrefix(project.Id));
            var row = await store.UpdateAsync(site.Db, project.Id, collection, rid, patch) ?? throw ApiException.NotFound("العنصر غير موجود");
            return Results.Ok(RecordStore.Shape(def, row, user, false));
        });

        s.MapDelete("/{rid:guid}", async (string siteKey, string collection, Guid rid, HttpContext http, SiteContext site, RecordStore store) =>
        {
            var (project, def) = await ResolveAsync(site, siteKey, collection, http);
            await RequireOwnerOfAsync(site, http, project, def, def.Access.Delete, rid);
            await store.DeleteAsync(site.Db, project.Id, collection, rid);
            return Results.NoContent();
        });

        // ---------- Owner dashboard ----------
        var d = app.MapGroup("/api/projects/{id:guid}/data/collections").RequireAuthorization();

        d.MapGet("", async (Guid id, HttpContext http, ProjectService projects, SiteContext site) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            var config = await site.ConfigAsync(project.CurrentVersionId);
            var counts = await site.Db.SiteRecords.Where(r => r.ProjectId == id).GroupBy(r => r.Collection)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            return Results.Ok(config.Collections.Values.Select(c => new
            {
                c.Name, label = c.Label ?? c.Name, count = counts.GetValueOrDefault(c.Name),
                access = c.Access,
                fields = c.Fields.Select(f => new { name = f.Key, f.Value.Type, label = f.Value.Label ?? f.Key, f.Value.Required, f.Value.Options, f.Value.Private, f.Value.Readonly })
            }));
        });

        d.MapGet("/{collection}", async (Guid id, string collection, string? q, int? page, HttpContext http, ProjectService projects, SiteContext site, RecordStore store) =>
        {
            var (_, def) = await OwnedAsync(id, collection, http, projects, site);
            var rows = await store.LoadAsync(site.Db, id, collection);
            var (items, total) = RecordStore.Query(rows, def, new RecordQuery(Search: q, Page: page ?? 1, PageSize: 50), admin: true);
            return Results.Ok(new { items = items.Select(r => RecordStore.Shape(def, r, null, true)), total });
        });

        d.MapPost("/{collection}", async (Guid id, string collection, JsonObject body, HttpContext http, ProjectService projects,
            SiteContext site, RecordStore store, UploadService uploads) =>
        {
            var (_, def) = await OwnedAsync(id, collection, http, projects, site);
            var data = BackendConfig.ValidateData(def, body, partial: false, admin: true, uploads.PublicPrefix(id));
            var row = await store.CreateAsync(site.Db, id, collection, data, null);
            return Results.Ok(RecordStore.Shape(def, row, null, true));
        });

        d.MapPatch("/{collection}/{rid:guid}", async (Guid id, string collection, Guid rid, JsonObject body, HttpContext http,
            ProjectService projects, SiteContext site, RecordStore store, UploadService uploads) =>
        {
            var (_, def) = await OwnedAsync(id, collection, http, projects, site);
            var patch = BackendConfig.ValidateData(def, body, partial: true, admin: true, uploads.PublicPrefix(id));
            var row = await store.UpdateAsync(site.Db, id, collection, rid, patch) ?? throw ApiException.NotFound();
            return Results.Ok(RecordStore.Shape(def, row, null, true));
        });

        d.MapDelete("/{collection}/{rid:guid}", async (Guid id, string collection, Guid rid, HttpContext http, ProjectService projects,
            SiteContext site, RecordStore store) =>
        {
            await OwnedAsync(id, collection, http, projects, site);
            await store.DeleteAsync(site.Db, id, collection, rid);
            return Results.NoContent();
        });
    }

    private static async Task<(SiteRef, CollectionDef)> ResolveAsync(SiteContext site, string siteKey, string collection, HttpContext http)
    {
        var project = await site.ResolveAsync(siteKey);
        site.EnsureBackend(project, http);
        var config = await site.ConfigAsync(SiteContext.VersionFor(project, http));
        if (!config.Collections.TryGetValue(collection, out var def)) throw ApiException.NotFound($"القائمة '{collection}' غير موجودة");
        return (project, def);
    }

    private static async Task<(Domain.Project, CollectionDef)> OwnedAsync(Guid id, string collection, HttpContext http, ProjectService projects, SiteContext site)
    {
        var project = await projects.GetOwnedAsync(id, http.User.UserId());
        var config = await site.ConfigAsync(project.CurrentVersionId);
        if (!config.Collections.TryGetValue(collection, out var def)) throw ApiException.NotFound($"القائمة '{collection}' غير موجودة");
        return (project, def);
    }

    private static async Task<Guid?> CanReadAsync(SiteContext site, HttpContext http, SiteRef project, CollectionDef def)
    {
        switch (def.Access.Read)
        {
            case Access.Public: return await site.SiteUserIdAsync(http, project);
            case Access.Users:
            case Access.Owner: return (await site.RequireUserAsync(http, project)).Id;
            default: throw ApiException.Forbidden("هذه البيانات لصاحب الموقع فقط");
        }
    }

    private static async Task<Guid> RequireOwnerOfAsync(SiteContext site, HttpContext http, SiteRef project, CollectionDef def, string rule, Guid rid)
    {
        if (rule != Access.Owner) throw ApiException.Forbidden("التعديل هنا لصاحب الموقع فقط");
        var user = await site.RequireUserAsync(http, project);
        var owner = await site.Db.SiteRecords.Where(r => r.Id == rid && r.ProjectId == project.Id && r.Collection == def.Name)
            .Select(r => new { r.SiteUserId }).FirstOrDefaultAsync() ?? throw ApiException.NotFound("العنصر غير موجود");
        if (owner.SiteUserId != user.Id) throw ApiException.Forbidden("لا يمكنك تعديل عنصر لا يخصك");
        return user.Id;
    }

    private static JsonObject? ParseWhere(string? where)
    {
        if (string.IsNullOrWhiteSpace(where)) return null;
        if (where.Length > 4000) throw ApiException.BadRequest("شرط التصفية طويل جداً");
        try { return JsonNode.Parse(where) as JsonObject ?? throw ApiException.BadRequest("شرط التصفية غير صالح"); }
        catch (JsonException) { throw ApiException.BadRequest("شرط التصفية غير صالح"); }
    }
}
