function check(condition, diagnostic) {
  if (!condition) throw new Error(diagnostic)
}

// Consume only the installed public bootstrap, independently built plugin, and public diagnostics.
export async function verifySharedModules(bootClientModules, graph, loadBundle, missing = false) {
  const shell = { root: { name: 'page-owned singleton' }, tools: { part: {} }, starts: 0, stops: 0 }
  const staticModules = { '@example/shell': shell.root }
  if (!missing) staticModules['@example/shell/tools'] = shell.tools
  const boot = () => bootClientModules({
    graph, staticModules,
    ...(loadBundle === undefined ? {} : { loadBundle }),
    configure(context) { context.provide('shell', shell) },
  })
  if (missing) {
    try { await boot() } catch (error) {
      check(String(error).includes('@example/shell/tools') && shell.starts === 0,
        'Missing subpath supplier was not diagnosed without activation')
      check(globalThis.__ModuleLoader__ === undefined, 'Rejected bootstrap retained the facade')
      return 'PASS missing exact supplier refused at startup'
    }
    throw new Error('Missing supplier did not reject startup')
  }
  const client = await boot()
  try {
    const failures = client.state.getSnapshot().failures
    check(failures.length === 0, 'Plugin activation failed: ' + JSON.stringify(failures))
    const initial = client.context.get('shared-result')
    check(initial.root === shell.root && initial.part === shell.tools.part, 'Plugin did not receive shell objects')
    await client.disconnect()
    check(shell.starts === 1 && shell.stops === 1, 'Withdrawal did not release the plugin effect')
    await client.sync(graph)
    const reloaded = client.context.get('shared-result')
    check(reloaded.root === shell.root && reloaded.part === shell.tools.part, 'Reload replaced shell singletons')
    check(shell.starts === 2 && shell.stops === 1, 'Reload did not activate a fresh plugin')
    await client.dispose()
    check(shell.stops === 2 && shell.root.name === 'page-owned singleton', 'Disposal damaged shell ownership')
    return 'PASS shell identity, subpath, withdrawal, reload and disposal'
  } finally {
    await client.dispose()
  }
}
