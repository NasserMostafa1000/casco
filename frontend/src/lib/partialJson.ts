type Frame = { close: '}' | ']'; expect: 'key' | 'colon' | 'value' | 'comma' }

/**
 * Parses JSON that is still being streamed: unfinished strings are closed, dangling keys/commas dropped,
 * and open objects/arrays closed. Returns undefined when nothing usable can be recovered yet.
 */
export function parsePartialJson(input: string): unknown {
  const start = input.indexOf('{')
  if (start < 0) return undefined
  const s = input.slice(start)
  const stack: Frame[] = []
  let inStr = false
  let esc = false
  let strIsKey = false
  let strStart = 0
  let cut = -1

  for (let i = 0; i < s.length; i++) {
    const c = s[i]
    if (inStr) {
      if (esc) esc = false
      else if (c === '\\') esc = true
      else if (c === '"') {
        inStr = false
        const top = stack[stack.length - 1]
        if (top) top.expect = strIsKey ? 'colon' : 'comma'
      }
      continue
    }
    const top = stack[stack.length - 1]
    if (c === '"') {
      inStr = true
      strStart = i
      strIsKey = !!top && top.close === '}' && top.expect === 'key'
    } else if (c === '{' || c === '[') {
      stack.push({ close: c === '{' ? '}' : ']', expect: c === '{' ? 'key' : 'value' })
    } else if (c === '}' || c === ']') {
      stack.pop()
      const parent = stack[stack.length - 1]
      if (!parent) return tryParse(s.slice(0, i + 1))
      parent.expect = 'comma'
    } else if (c === ':') {
      if (top) top.expect = 'value'
    } else if (c === ',') {
      if (top) top.expect = top.close === '}' ? 'key' : 'value'
    } else if (top && top.expect === 'value' && /[-0-9tfn]/.test(c)) {
      let j = i
      while (j < s.length && /[\w.+-]/.test(s[j])) j++
      if (j >= s.length) {
        cut = i
        break
      }
      top.expect = 'comma'
      i = j - 1
    }
  }

  let out: string
  if (cut >= 0) out = s.slice(0, cut)
  else if (inStr && strIsKey) out = s.slice(0, strStart)
  else if (inStr) {
    out = esc ? s.slice(0, -1) : s
    out = out.replace(/\\u[0-9a-fA-F]{0,3}$/, '') + '"'
    const top = stack[stack.length - 1]
    if (top) top.expect = 'comma'
  } else out = s

  out = out.trimEnd()
  const top = stack[stack.length - 1]
  if (out.endsWith(',')) out = out.slice(0, -1)
  else if (out.endsWith(':')) out += 'null'
  else if (!inStr && cut < 0 && top?.expect === 'colon') out += ':null'

  for (let k = stack.length - 1; k >= 0; k--) out += stack[k].close
  return tryParse(out)
}

function tryParse(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return undefined
  }
}
