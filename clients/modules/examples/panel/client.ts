import type { Context } from '@deepseek-ai/cordis'
import { greeting } from 'example-provider/client'
import type { SlotCore } from '@cordis-net/client-modules'

declare module '@cordis-net/client-modules/slots' {
  interface SlotMap { 'example.panel': { kind: 'list'; scope: 'root' } }
}

export const inject = ['greeting', 'slots']
export function apply(ctx: Context): void {
  const slots: SlotCore = ctx.get('slots')
  ctx.effect(() => slots.register({ name: 'example.panel', id: 'panel' }, () => greeting()))
  ctx.effect(() => {
    const panel = document.createElement('section')
    panel.id = 'plugin-panel'
    const button = document.createElement('button')
    button.textContent = greeting()
    let clicks = 0
    const onClick = (): void => { button.textContent = `${greeting()} clicked ${++clicks}` }
    button.addEventListener('click', onClick)
    panel.append(button)
    document.querySelector('#contributions')?.append(panel)
    return () => { button.removeEventListener('click', onClick); panel.remove() }
  })
}
