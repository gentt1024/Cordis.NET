import type { Context } from '@deepseek-ai/cordis'
import Loader from '@deepseek-ai/cordis-plugin-loader'
import type { BootManifest, ClientModuleLoader } from '@cordis-net/modules/manifest'

/** Activate the supplied browser graph on the shell's own Cordis root. */
export async function bootClient({ ctx, modules, manifest }: {
  ctx: Context; modules: ClientModuleLoader; manifest: BootManifest
}): Promise<void> {
  await ctx.plugin(Loader)
  // The Loader's published type describes Node's internal loader only. Its browser
  // import path consumes the client module system through the same property.
  Object.assign(ctx.loader, { internal: modules })
  await modules.entries.start(ctx.loader, manifest)
  await ctx.loader.await()
  const failures: string[] = []
  // FiberState is a const enum with no runtime export in the published package.
  const states = ['pending', 'loading', 'active', 'failed', 'disposed', 'unloading']
  for (const entry of ctx.loader.entries()) {
    const { name } = entry.options
    if (!entry.fiber) {
      failures.push(`${name}: import failed: ${modules.importError(name)?.message ?? 'see console'}`)
      continue
    }
    const state = states[entry.fiber.state]
    if (state === 'active') continue
    const missing = Object.keys(entry.fiber.inject).filter(key => ctx.get(key) === undefined)
    failures.push(`${name}: ${state}${state === 'pending' ? ' (waiting for services: ' + missing.join(', ') + ')' : ''}`)
  }
  if (failures.length) throw new Error('Client entries did not activate\n' + failures.join('\n'))
}
