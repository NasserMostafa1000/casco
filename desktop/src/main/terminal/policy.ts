export type CommandSource = 'user' | 'ai'
export type CommandDecision = 'ALLOW' | 'ASK_USER' | 'DENY'

/**
 * User commands are a real local shell and are not rewritten.
 * AI commands are classified here. The caller cannot skip this check.
 */
export function validateCommand(input: { command: string; cwd: string; source: CommandSource }): CommandDecision {
  if (!input.command.trim() || !input.cwd.trim()) return 'DENY'
  if (input.source === 'user') return 'ALLOW'
  return reviewAiCommand(input.command)
}

function reviewAiCommand(command: string): CommandDecision {
  const text = command.toLowerCase()
  const deny = ['mkfs', 'format c:', ':(){', 'shutdown', 'reboot', 'diskpart']
  const ask = [
    'rm ',
    'rm\t',
    'rmdir',
    'remove-item',
    'del ',
    'git reset --hard',
    'git clean',
    'git push',
    'sudo ',
    'chmod ',
    'chown ',
    'set-executionpolicy',
    'invoke-webrequest',
    'invoke-expression',
    'iex ',
    'iwr ',
    'curl ',
    'wget ',
    'npm install -g',
    '../',
    '..\\',
  ]
  if (deny.some((part) => text.includes(part))) return 'DENY'
  if (ask.some((part) => text.includes(part))) return 'ASK_USER'
  return 'ALLOW'
}
