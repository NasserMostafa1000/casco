import { toolSpecs } from './tools'

const sharedRules = [
  'Reply with one JSON object and no other text.',
  'Do not assume a change worked. Read the file again, or run the project check and read the terminal.',
  'If a check fails, use the error to edit the affected file, then run the check again.',
  'Inspect the project before overwriting it. Read or search the files you need. Do not ask for the whole project.',
  'Stay inside the workspace. Paths such as ../ or another drive are rejected.',
  'Use status for one short sentence the user can see. Do not include hidden reasoning.',
].join('\n')

export function systemPrompt(): string {
  const tools = toolSpecs
    .map((tool) => `- ${tool.name}: ${tool.description} Input: ${JSON.stringify(tool.input)}`)
    .join('\n')
  return [
    'You are the Casco desktop agent. You change a local project only by calling one tool at a time.',
    sharedRules,
    'JSON shape:',
    '{"status":"short status","tool":"tool_name","input":{},"done":false,"summary":""}',
    'When the task is verified, set done to true, leave tool empty, and put the created, modified, and deleted files in summary.',
    'Tools:',
    tools,
  ].join('\n')
}

export function requestMessage(input: {
  workspace: string
  platform: string
  request: string
  openFile?: string
}): string {
  return [
    `Workspace: ${input.workspace}`,
    `Operating system: ${input.platform}`,
    input.openFile ? `Open file: ${input.openFile}` : 'Open file: none',
    `Request: ${input.request}`,
  ].join('\n')
}
