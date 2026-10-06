using Casco.Api.Infrastructure.Email;
using static Casco.Api.Infrastructure.Email.EmailText;

namespace Casco.Api.Features.Notifications;

public enum SiteEventKind { Order, Booking, Message }

/// <summary>Wording of every customer e-mail in Arabic, English and Hindi.</summary>
public static class EmailCopy
{
    private static string Hello(string lang, string name) =>
        L(lang, $"مرحباً {name}،", $"Hi {name},", $"नमस्ते {name},");

    private static string Reminder(string lang) => L(lang, "تذكير", "Reminder", "रिमाइंडर");

    /// <summary>Reminders go out in Arabic and English together; Arabic readers see Arabic first, everyone else English.</summary>
    public static EmailContent[] ArabicAndEnglish(string? readerLang, Func<string, EmailContent> build) =>
        Normalize(readerLang) == "ar" ? [build("ar"), build("en")] : [build("en"), build("ar")];

    public static EmailContent ProExpiring(string lang, string name, DateTime end, int hoursLeft, string price, int monthlyCredits, string billingUrl)
    {
        var tomorrow = hoursLeft <= 24;
        var days = Math.Max(1, (int)Math.Ceiling(hoursLeft / 24.0));
        var date = Date(end, lang);
        return new EmailContent
        {
            Lang = lang,
            Tone = tomorrow ? EmailTone.Warning : EmailTone.Brand,
            Badge = Reminder(lang),
            Subject = tomorrow
                ? L(lang, "اشتراكك في Casco Pro ينتهي غداً", "Your Casco Pro plan ends tomorrow", "आपका Casco Pro प्लान कल समाप्त हो रहा है")
                : L(lang, $"اشتراكك في Casco Pro ينتهي خلال {days} أيام", $"Your Casco Pro plan ends in {days} days", $"आपका Casco Pro प्लान {days} दिनों में समाप्त हो रहा है"),
            Preheader = L(lang, $"جدّد قبل {date} لتستمر في تعديل مواقعك.", $"Renew before {date} to keep editing your sites.", $"अपनी साइटें एडिट करते रहने के लिए {date} से पहले रिन्यू करें।"),
            Title = L(lang, "اشتراكك ينتهي قريباً", "Your Pro plan is ending soon", "आपका Pro प्लान जल्द समाप्त हो रहा है"),
            Paragraphs =
            [
                Hello(lang, name) + " " + L(lang,
                    $"اشتراكك في Casco Pro ينتهي يوم {date}. جدّده الآن لتستمر في تعديل مواقعك بالكلام العادي وتحصل على {Number(monthlyCredits)} نقطة كل شهر.",
                    $"your Casco Pro plan ends on {date}. Renew now to keep editing your sites in plain language and get {Number(monthlyCredits)} credits every month.",
                    $"आपका Casco Pro प्लान {date} को समाप्त हो रहा है। अपनी साइटें सामान्य भाषा में एडिट करते रहने और हर महीने {Number(monthlyCredits)} क्रेडिट पाने के लिए अभी रिन्यू करें।"),
                L(lang, "مواقعك المنشورة تبقى تعمل طالما أن استضافتها مدفوعة.", "Your published sites stay online as long as their hosting is paid.", "आपकी प्रकाशित साइटें तब तक ऑनलाइन रहेंगी जब तक उनकी होस्टिंग का भुगतान है।")
            ],
            Facts =
            [
                new(L(lang, "الخطة", "Plan", "प्लान"), "Casco Pro"),
                new(L(lang, "تنتهي في", "Ends on", "समाप्ति तिथि"), date),
                new(L(lang, "سعر التجديد", "Renewal price", "रिन्यूअल कीमत"), price)
            ],
            Button = new(L(lang, "جدّد الاشتراك", "Renew Pro", "Pro रिन्यू करें"), billingUrl)
        };
    }

    public static EmailContent ProExpired(string lang, string name, DateTime end, string billingUrl) => new()
    {
        Lang = lang,
        Tone = EmailTone.Danger,
        Badge = L(lang, "انتهى الاشتراك", "Plan ended", "प्लान समाप्त"),
        Subject = L(lang, "انتهى اشتراكك في Casco Pro", "Your Casco Pro plan has ended", "आपका Casco Pro प्लान समाप्त हो गया है"),
        Preheader = L(lang, "اشترك من جديد لتعود التعديلات والنقاط الشهرية.", "Resubscribe to get editing and monthly credits back.", "एडिटिंग और मासिक क्रेडिट वापस पाने के लिए फिर से सब्सक्राइब करें।"),
        Title = L(lang, "انتهى اشتراكك في Pro", "Your Pro plan has ended", "आपका Pro प्लान समाप्त हो गया है"),
        Paragraphs =
        [
            Hello(lang, name) + " " + L(lang,
                $"انتهى اشتراكك في Casco Pro يوم {Date(end, lang)}، فتوقفت التعديلات والنقاط الشهرية.",
                $"your Casco Pro plan ended on {Date(end, lang)}, so editing and monthly credits are paused.",
                $"आपका Casco Pro प्लान {Date(end, lang)} को समाप्त हो गया, इसलिए एडिटिंग और मासिक क्रेडिट रुक गए हैं।"),
            L(lang, "مواقعك ومحتواها محفوظة، والمواقع المنشورة تبقى تعمل طالما أن استضافتها مدفوعة. اشترك من جديد في أي وقت لتكمل من حيث توقفت.",
                "Your sites and content are safe, and published sites stay online while their hosting is paid. Resubscribe any time to pick up where you left off.",
                "आपकी साइटें और कंटेंट सुरक्षित हैं, और प्रकाशित साइटें होस्टिंग के भुगतान तक ऑनलाइन रहेंगी। कभी भी फिर से सब्सक्राइब करें और वहीं से शुरू करें।")
        ],
        Button = new(L(lang, "اشترك من جديد", "Resubscribe to Pro", "फिर से सब्सक्राइब करें"), billingUrl)
    };

    private static List<EmailFact> SiteFacts(string lang, string site, string url) =>
    [
        new(L(lang, "الموقع", "Site", "साइट"), site),
        new(L(lang, "الرابط", "Address", "पता"), url.Replace("https://", "").Replace("http://", ""), true)
    ];

    public static EmailContent HostingExpiring(string lang, string name, string site, string url, DateTime paidUntil, int hoursLeft, string price, string renewUrl)
    {
        var tomorrow = hoursLeft <= 24;
        var days = Math.Max(1, (int)Math.Ceiling(hoursLeft / 24.0));
        var date = Date(paidUntil, lang);
        var facts = SiteFacts(lang, site, url);
        facts.Add(new(L(lang, "مدفوعة حتى", "Paid until", "भुगतान तक"), date));
        facts.Add(new(L(lang, "سعر التجديد", "Renewal price", "रिन्यूअल कीमत"), price));
        return new EmailContent
        {
            Lang = lang,
            Tone = tomorrow ? EmailTone.Warning : EmailTone.Brand,
            Badge = Reminder(lang),
            Subject = tomorrow
                ? L(lang, $"استضافة موقع {site} تنتهي غداً", $"Hosting for {site} ends tomorrow", $"{site} की होस्टिंग कल समाप्त हो रही है")
                : L(lang, $"استضافة موقع {site} تنتهي خلال {days} أيام", $"Hosting for {site} ends in {days} days", $"{site} की होस्टिंग {days} दिनों में समाप्त हो रही है"),
            Preheader = L(lang, $"جدّد قبل {date} حتى يبقى موقعك متاحاً.", $"Renew before {date} to keep your site online.", $"साइट ऑनलाइन रखने के लिए {date} से पहले रिन्यू करें।"),
            Title = L(lang, "جدّد استضافة موقعك", "Renew your site's hosting", "अपनी साइट की होस्टिंग रिन्यू करें"),
            Paragraphs =
            [
                Hello(lang, name) + " " + L(lang,
                    $"استضافة موقعك {site} مدفوعة حتى {date}. جدّدها قبل هذا التاريخ حتى يبقى موقعك متاحاً لزوارك بدون انقطاع.",
                    $"hosting for {site} is paid until {date}. Renew before then so your visitors never find it offline.",
                    $"{site} की होस्टिंग {date} तक भुगतान की गई है। इससे पहले रिन्यू करें ताकि आपकी साइट बिना रुकावट चलती रहे।")
            ],
            Facts = facts,
            Button = new(L(lang, "جدّد الاستضافة", "Renew hosting", "होस्टिंग रिन्यू करें"), renewUrl)
        };
    }

    public static EmailContent ReactHostingSoon(string lang, string name, string site, string url, DateTime paidUntil, string renewUrl)
    {
        var facts = SiteFacts(lang, site, url);
        facts.Add(new(L(lang, "تنتهي في", "Ends on", "समाप्ति तिथि"), Date(paidUntil, lang)));
        facts.Add(new(L(lang, "التجديد", "Renewal", "रिन्यूअल"), L(lang, "$10 في السنة", "$10 per year", "$10 प्रति वर्ष")));
        return new EmailContent
        {
            Lang = lang,
            Tone = EmailTone.Warning,
            Badge = Reminder(lang),
            Subject = L(lang, "استضافة موقعك على Casco قربت تخلص", "Your Casco hosting is about to end", "आपकी Casco होस्टिंग जल्द समाप्त हो रही है"),
            Preheader = L(lang, "ادخل على Casco وجدّدها قبل ما يتوقف الموقع.", "Open Casco and renew before the site goes offline.", "साइट बंद होने से पहले Casco पर जाकर रिन्यू करें।"),
            Title = L(lang, "استضافتك قربت تخلص", "Your hosting is about to end", "आपकी होस्टिंग जल्द समाप्त हो रही है"),
            Paragraphs =
            [
                Hello(lang, name) + " " + L(lang,
                    $"استضافة موقعك {site} قربت تخلص (خلال يومين). ادخل على Casco وجدّدها قبل ما يتوقف الموقع.",
                    $"hosting for {site} ends in two days. Open Casco and renew it before the site goes offline.",
                    $"{site} की होस्टिंग दो दिनों में समाप्त हो रही है। साइट बंद होने से पहले Casco पर जाकर रिन्यू करें।")
            ],
            Facts = facts,
            Button = new(L(lang, "ادخل على Casco وجدّدها", "Open Casco and renew", "Casco पर जाकर रिन्यू करें"), renewUrl)
        };
    }

    public static EmailContent ReactHostingStopped(string lang, string name, string site, string url, string renewUrl)
    {
        var facts = SiteFacts(lang, site, url);
        return new EmailContent
        {
            Lang = lang,
            Tone = EmailTone.Danger,
            Badge = L(lang, "الموقع متوقف", "Site offline", "साइट बंद"),
            Subject = L(lang, $"موقع {site} اتوقف لأن الاستضافة خلصت", $"{site} is offline because hosting ended", $"{site} बंद है क्योंकि होस्टिंग समाप्त हो गई"),
            Preheader = L(lang, "ادخل على Casco وجدّد الاستضافة عشان الموقع يرجع.", "Open Casco and renew hosting to bring the site back.", "साइट वापस लाने के लिए Casco पर जाकर होस्टिंग रिन्यू करें।"),
            Title = L(lang, "الموقع اتوقف", "The site is offline", "साइट बंद है"),
            Paragraphs =
            [
                Hello(lang, name) + " " + L(lang,
                    $"استضافة موقعك {site} خلصت، والموقع اتوقف. ادخل على Casco وجدّدها عشان يرجع يشتغل. ملفات التطبيق محفوظة.",
                    $"hosting for {site} has ended, so the site is offline. Open Casco and renew it to bring the site back. Your build is still saved.",
                    $"{site} की होस्टिंग समाप्त हो गई, इसलिए साइट बंद है। वापस लाने के लिए Casco पर जाकर रिन्यू करें। आपका बिल्ड सुरक्षित है।")
            ],
            Facts = facts,
            Button = new(L(lang, "جدّد الاستضافة", "Renew hosting", "होस्टिंग रिन्यू करें"), renewUrl)
        };
    }

    public static EmailContent HostingGrace(string lang, string name, string site, string url, DateTime offlineAt, string renewUrl)
    {
        var date = Date(offlineAt, lang);
        var facts = SiteFacts(lang, site, url);
        facts.Add(new(L(lang, "يتوقف يوم", "Goes offline on", "बंद होने की तिथि"), date));
        return new EmailContent
        {
            Lang = lang,
            Tone = EmailTone.Warning,
            Badge = L(lang, "إجراء مطلوب", "Action needed", "कार्रवाई ज़रूरी"),
            Subject = L(lang, $"انتهت استضافة {site} — سيتوقف الموقع يوم {date}", $"Hosting for {site} expired — it goes offline on {date}", $"{site} की होस्टिंग समाप्त — साइट {date} को बंद हो जाएगी"),
            Preheader = L(lang, "جدّد الآن حتى لا يتوقف موقعك.", "Renew now so your site stays online.", "अभी रिन्यू करें ताकि साइट बंद न हो।"),
            Title = L(lang, "موقعك في فترة السماح", "Your site is in its grace period", "आपकी साइट ग्रेस पीरियड में है"),
            Paragraphs =
            [
                Hello(lang, name) + " " + L(lang,
                    $"انتهت استضافة موقعك {site}، وأبقيناه يعمل حتى {date}. جدّد الآن حتى لا يتوقف.",
                    $"hosting for {site} has expired. We've kept it online until {date} — renew now so it doesn't go offline.",
                    $"{site} की होस्टिंग समाप्त हो गई है। हमने इसे {date} तक चालू रखा है — अभी रिन्यू करें ताकि साइट बंद न हो।")
            ],
            Facts = facts,
            Button = new(L(lang, "جدّد الاستضافة الآن", "Renew hosting now", "अभी होस्टिंग रिन्यू करें"), renewUrl)
        };
    }

    public static EmailContent HostingStopped(string lang, string name, string site, string url, string renewUrl) => new()
    {
        Lang = lang,
        Tone = EmailTone.Danger,
        Badge = L(lang, "الموقع متوقف", "Site offline", "साइट ऑफ़लाइन"),
        Subject = L(lang, $"موقع {site} متوقف الآن", $"{site} is offline", $"{site} अब ऑफ़लाइन है"),
        Preheader = L(lang, "جدّد الاستضافة ليعود موقعك فوراً.", "Renew hosting to bring your site back instantly.", "साइट तुरंत वापस लाने के लिए होस्टिंग रिन्यू करें।"),
        Title = L(lang, "توقف موقعك", "Your site is offline", "आपकी साइट ऑफ़लाइन है"),
        Paragraphs =
        [
            Hello(lang, name) + " " + L(lang,
                $"توقف موقعك {site} لأن الاستضافة لم تُجدَّد. محتوى الموقع وبياناته محفوظة، وسيعود فور تجديد الاستضافة.",
                $"{site} is offline because its hosting wasn't renewed. Your content and data are safe — the site comes back as soon as you renew.",
                $"{site} ऑफ़लाइन है क्योंकि होस्टिंग रिन्यू नहीं हुई। आपका कंटेंट और डेटा सुरक्षित है — रिन्यू करते ही साइट वापस आ जाएगी।")
        ],
        Facts = SiteFacts(lang, site, url),
        Button = new(L(lang, "أعد تشغيل الموقع", "Bring it back online", "साइट फिर से चालू करें"), renewUrl)
    };

    public static EmailContent CreditsLow(string lang, string name, int available, DateTime? refillAt, string billingUrl)
    {
        var refill = refillAt is { } r ? Date(r, lang) : null;
        var facts = new List<EmailFact> { new(L(lang, "النقاط المتبقية", "Credits left", "बचे क्रेडिट"), Number(available)) };
        if (refill is not null) facts.Add(new(L(lang, "تتجدد يوم", "Refills on", "रिफ़िल तिथि"), refill));
        return new EmailContent
        {
            Lang = lang,
            Tone = EmailTone.Warning,
            Badge = L(lang, "النقاط", "Credits", "क्रेडिट"),
            Subject = L(lang, "نقاطك قاربت على النفاد", "You're running low on credits", "आपके क्रेडिट लगभग खत्म हो गए हैं"),
            Preheader = L(lang, "اشترِ نقاطاً إضافية حتى لا تتوقف تعديلاتك.", "Top up so your edits don't stop.", "टॉप-अप करें ताकि आपके एडिट न रुकें।"),
            Title = L(lang, "نقاطك قاربت على النفاد", "You're running low on credits", "आपके क्रेडिट लगभग खत्म हो गए हैं"),
            Paragraphs =
            [
                Hello(lang, name) + " " + (refill is null
                    ? L(lang, $"بقي لديك {Number(available)} نقطة فقط. اشترِ نقاطاً إضافية الآن حتى لا تتوقف تعديلاتك.",
                        $"you have only {Number(available)} credits left. Top up now so your edits don't stop.",
                        $"आपके पास केवल {Number(available)} क्रेडिट बचे हैं। अभी टॉप-अप करें ताकि आपके एडिट न रुकें।")
                    : L(lang, $"بقي لديك {Number(available)} نقطة فقط هذا الشهر. تتجدد نقاطك يوم {refill}، ويمكنك شراء نقاط إضافية الآن حتى لا تتوقف تعديلاتك.",
                        $"you have only {Number(available)} credits left this month. Your credits refill on {refill} — top up now so your edits don't stop.",
                        $"इस महीने आपके पास केवल {Number(available)} क्रेडिट बचे हैं। आपके क्रेडिट {refill} को रिफ़िल होंगे — अभी टॉप-अप करें ताकि आपके एडिट न रुकें।"))
            ],
            Facts = facts,
            Button = new(L(lang, "اشترِ نقاطاً", "Buy credits", "क्रेडिट खरीदें"), billingUrl)
        };
    }

    public static EmailContent Receipt(string lang, string name, string item, string amount, DateTime paidAt, DateTime? validUntil,
        string reference, bool isTest, string billingUrl)
    {
        var facts = new List<EmailFact>
        {
            new(L(lang, "الخدمة", "Item", "सेवा"), item),
            new(L(lang, "المبلغ", "Amount", "राशि"), amount, true),
            new(L(lang, "التاريخ", "Date", "तिथि"), Date(paidAt, lang))
        };
        if (validUntil is { } until) facts.Add(new(L(lang, "صالحة حتى", "Valid until", "मान्य तक"), Date(until, lang)));
        facts.Add(new(L(lang, "رقم العملية", "Reference", "संदर्भ संख्या"), reference, true));
        return new EmailContent
        {
            Lang = lang,
            Tone = EmailTone.Success,
            Badge = isTest ? L(lang, "دفعة تجريبية", "Test payment", "टेस्ट भुगतान") : L(lang, "تم الدفع", "Paid", "भुगतान हुआ"),
            Subject = L(lang, $"إيصال الدفع — {item}", $"Payment receipt — {item}", $"भुगतान रसीद — {item}"),
            Preheader = L(lang, $"استلمنا {amount} — شكراً لك.", $"We received {amount} — thank you.", $"हमें {amount} मिल गए — धन्यवाद।"),
            Title = L(lang, "شكراً، تم الدفع بنجاح", "Thanks — your payment went through", "धन्यवाद — आपका भुगतान सफल रहा"),
            Paragraphs = [Hello(lang, name) + " " + L(lang, $"استلمنا دفعتك وتم تفعيل {item}.", $"we've received your payment and activated {item}.", $"हमें आपका भुगतान मिल गया है और {item} सक्रिय कर दिया गया है।")],
            Facts = facts,
            Button = new(L(lang, "عرض الفواتير", "View billing", "बिलिंग देखें"), billingUrl),
            Note = L(lang, "احتفظ بهذه الرسالة كإيصال لعملية الدفع.", "Keep this email as your receipt.", "इस ईमेल को अपनी रसीद के रूप में रखें।")
        };
    }

    public static string ProItem(string lang, string? interval) => interval == "yearly"
        ? L(lang, "Casco Pro — سنوي", "Casco Pro — yearly", "Casco Pro — वार्षिक")
        : L(lang, "Casco Pro — شهري", "Casco Pro — monthly", "Casco Pro — मासिक");

    public static string HostingItem(string lang, string site) => L(lang, $"استضافة {site}", $"Hosting for {site}", $"{site} की होस्टिंग");

    public static string TopupItem(string lang, int credits) =>
        L(lang, $"{Number(credits)} نقطة إضافية", $"{Number(credits)} extra credits", $"{Number(credits)} अतिरिक्त क्रेडिट");

    /// <summary>New order / booking / form message on a customer's site. "Label: value" lines become a table.</summary>
    public static EmailContent SiteEvent(string lang, string site, SiteEventKind kind, int? number, string summary, string dashboardUrl)
    {
        var title = kind switch
        {
            SiteEventKind.Order => number is { } n
                ? L(lang, $"طلب جديد #{n}", $"New order #{n}", $"नया ऑर्डर #{n}")
                : L(lang, "طلب جديد", "New order", "नया ऑर्डर"),
            SiteEventKind.Booking => L(lang, "حجز جديد", "New booking", "नई बुकिंग"),
            _ => L(lang, "رسالة جديدة من موقعك", "New message from your site", "आपकी साइट से नया संदेश")
        };
        var facts = new List<EmailFact>();
        var lines = new List<string>();
        foreach (var raw in summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = raw.IndexOf(": ", StringComparison.Ordinal);
            if (i > 0 && i < 40) facts.Add(new(raw[..i], raw[(i + 2)..]));
            else lines.Add(raw);
        }
        return new EmailContent
        {
            Lang = lang,
            Tone = EmailTone.Brand,
            Badge = site,
            Subject = $"[{site}] {title}",
            Preheader = lines.FirstOrDefault() ?? title,
            Title = title,
            Paragraphs = lines,
            Facts = facts,
            Button = new(L(lang, "افتح لوحة الموقع", "Open site dashboard", "साइट डैशबोर्ड खोलें"), dashboardUrl)
        };
    }
}
