# Casco Studio desktop

This folder is the previous Electron prototype and reference.

It is not the final Casco Desktop architecture. Do not continue implementing the production desktop here. The production desktop is the separate checkout at `D:\casco-studio` (VS Code tag `1.140.0`). It is outside this web repository and is not connected to this prototype. The path has no spaces because the upstream install script cannot run from `D:\nasser mostafa`.

The notes below describe only that prototype. The website in `frontend` is separate and is still what casco.studio deploys.

## Start Casco Web

```bash
cd frontend
npm run dev
```

Production web build, uploaded to Cloudflare Pages:

```bash
cd frontend
npm run build
```

Output: `frontend/dist`

## Start Casco Desktop

```bash
cd desktop
npm install
npm run dev
```

This compiles the Electron process and opens the desktop workspace at `http://127.0.0.1:5174`. It does not start the Casco website or the API.

Use **Open folder** to choose a project. The explorer, editor, and terminal then stay inside that folder.

## How the pieces connect

The React workspace is `desktop/src/renderer`. Electron's main process owns the disk and the shell. The page only receives `window.cascoDesktop`, built from a preload bridge. Node, `fs`, and `child_process` are not available to the page.

Filesystem calls are checked against the selected folder. A path that leaves the folder is rejected.

The terminal starts in that folder. On Windows the shell is PowerShell. On macOS it is zsh. A command typed by the person at the keyboard is a real shell: it can still reach other folders, because a shell is not a filesystem sandbox, and those commands are not rewritten.

Commands requested by the agent are marked as AI commands and pass through `validateCommand` in `desktop/src/main/terminal/policy.ts`. Safe project commands can run. Destructive, networked, or privilege-changing commands are denied or wait for Allow. The agent cannot skip that check, and it cannot read or write outside the open folder.

The desktop agent does not include Casco's server API keys. To use a live model, set `CASCO_MODEL_BASE_URL`, `CASCO_MODEL_API_KEY`, and `CASCO_MODEL_NAME` to an OpenAI-compatible chat endpoint before starting the app. The website agent on the server is unchanged.

A packaged build can load `desktop/dist-renderer` through the `casco://app` protocol. Create that folder with `npx vite build` inside `desktop`. Installers are prepared as `Casco-Setup.exe` and `Casco.dmg`, without signing.

## Files

Electron main process:

- `src/main.ts`
- `src/preload.ts`
- `src/main/ipc/register.ts`
- `src/main/filesystem/`
- `src/main/terminal/`
- `src/main/agent/`
- `src/main/workspace/`
- `src/shared/`

Desktop workspace UI:

- `src/renderer/`

No file under `frontend` is part of this workspace.
