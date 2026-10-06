export type ChatTurn = { role: 'system' | 'user' | 'assistant'; content: string }

export type ModelProvider = {
  complete(messages: ChatTurn[], signal: AbortSignal): Promise<string>
}

export function createProvider(request: string): ModelProvider {
  if (process.env.CASCO_DESKTOP_VERIFY === '1') return scriptedProvider(request)
  return liveProvider()
}

function liveProvider(): ModelProvider {
  return {
    async complete(messages, signal) {
      const base = process.env.CASCO_MODEL_BASE_URL?.replace(/\/$/, '')
      const key = process.env.CASCO_MODEL_API_KEY
      const model = process.env.CASCO_MODEL_NAME
      if (!base || !key || !model) {
        throw new Error('MODEL_NOT_CONFIGURED')
      }
      const response = await fetch(`${base}/chat/completions`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${key}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ model, messages, temperature: 0 }),
        signal,
      })
      if (!response.ok) throw new Error('MODEL_REQUEST_FAILED')
      const body = (await response.json()) as { choices?: Array<{ message?: { content?: string } }> }
      const content = body.choices?.[0]?.message?.content
      if (!content) throw new Error('MODEL_REQUEST_FAILED')
      return content
    },
  }
}

function scriptedProvider(request: string): ModelProvider {
  const steps = fixtureSteps(request.trim())
  let index = 0
  return {
    async complete() {
      const step = steps[Math.min(index, steps.length - 1)]
      index += 1
      return step
    },
  }
}

function fixtureSteps(request: string): string[] {
  const build = 'node build.js'
  if (request.startsWith('Create a hello.txt')) {
    return [
      step('Creating hello.txt', 'create_file', { path: 'hello.txt', content: 'Hello Casco\n' }),
      step('Reading hello.txt', 'read_file', { path: 'hello.txt' }),
      done('Created hello.txt'),
    ]
  }
  if (request.startsWith('Create a simple HTML page')) {
    return [
      step('Creating index.html', 'create_file', {
        path: 'index.html',
        content: '<!doctype html><html><body><h1>Hello</h1></body></html>\n',
      }),
      step('Reading index.html', 'read_file', { path: 'index.html' }),
      done('Created index.html'),
    ]
  }
  if (request.startsWith('Create a React component')) {
    return [
      step('Inspecting project', 'list_directory', { path: '.' }),
      step('Reading package.json', 'read_file', { path: 'package.json' }),
      step('Writing Widget.js', 'create_file', { path: 'src/Widget.js', content: 'export const Widget = (\n' }),
      step('Running build', 'run_command', { command: build }),
      step('Reading build output', 'read_terminal', {}),
      step('Fixing Widget.js', 'edit_file', {
        path: 'src/Widget.js',
        find: 'export const Widget = (',
        replace: "export function Widget() { return 'ok' }\n",
      }),
      step('Running build again', 'run_command', { command: build }),
      step('Reading build output', 'read_terminal', {}),
      done('Build successful.'),
    ]
  }
  if (request.startsWith('Fix the build error')) {
    return [
      step('Reading package.json', 'read_file', { path: 'package.json' }),
      step('Running build', 'run_command', { command: build }),
      step('Reading build output', 'read_terminal', {}),
      step('Reading Widget.js', 'read_file', { path: 'src/Widget.js' }),
      step('Fixing Widget.js', 'edit_file', {
        path: 'src/Widget.js',
        find: 'export const Widget = (',
        replace: "export function Widget() { return 'ok' }\n",
      }),
      step('Running build again', 'run_command', { command: build }),
      step('Reading build output', 'read_terminal', {}),
      done('Build successful.'),
    ]
  }
  if (request.startsWith('Run a long command')) {
    const command = process.platform === 'win32' ? 'Start-Sleep -Seconds 30' : 'sleep 30'
    return [
      step('Running a long command', 'run_command', { command }),
      step('Creating leaked.txt', 'create_file', { path: 'leaked.txt', content: 'no\n' }),
      done('This step must not run.'),
    ]
  }
  if (request.startsWith('Ask to run a dangerous command')) {
    return [
      step('Requesting a download', 'run_command', { command: 'curl https://example.invalid/casco-test' }),
      done('The command was not run.'),
    ]
  }
  if (request.startsWith('Read a file outside')) {
    return [
      step('Reading outside the project', 'read_file', { path: '../casco-outside.txt' }),
      done('Could not read outside the workspace.'),
    ]
  }
  return [done('This request is not supported by the desktop test model.')]
}

function step(status: string, tool: string, input: Record<string, unknown>): string {
  return JSON.stringify({ status, tool, input, done: false, summary: '' })
}

function done(summary: string): string {
  return JSON.stringify({ status: summary, done: true, summary })
}
