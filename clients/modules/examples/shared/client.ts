import type { Context } from '@cordis-net/client-modules'
import { part } from '@example/shell/tools'

// The shell supplies these modules; the author build must leave both requests external.
declare function require(specifier: '@example/shell'): ShellModules['root']
interface ShellModules {
  root: { name: string }
  tools: { part: object }
  starts: number
  stops: number
}

const root = require('@example/shell')

export function apply(ctx: Context): void {
  const shell = ctx.get('shell') as ShellModules
  if (root !== shell.root || part !== shell.tools.part) throw new Error('Shell module identity was lost')
  ctx.provide('shared-result', { root, part })
  ctx.effect(() => {
    shell.starts++
    return () => { shell.stops++ }
  })
}
