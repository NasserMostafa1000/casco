export class DesktopFailure extends Error {
  code: string

  constructor(code: string, message: string) {
    super(message)
    this.code = code
  }
}

export function asObject(value: unknown, code: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new DesktopFailure(code, 'The request was not valid.')
  }
  return value as Record<string, unknown>
}

export function asString(value: unknown, code: string, allowEmpty = false): string {
  if (typeof value !== 'string' || value.includes('\0')) {
    throw new DesktopFailure(code, 'The request was not valid.')
  }
  if (!allowEmpty && value.trim() === '') throw new DesktopFailure(code, 'The request was not valid.')
  return value
}
