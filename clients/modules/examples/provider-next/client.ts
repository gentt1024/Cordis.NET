import { Context, Service, SlotCore } from '@cordis-net/client-modules'
import type { PropsRenderSlots } from '@cordis-net/client-modules/slots'

declare module '@cordis-net/client-modules/slots' {
  interface SlotMap {
    root: { kind: 'single'; scope: 'root' }
    'example.panel': { kind: 'list'; scope: 'root' }
  }
}

class GreetingInfo extends Service {
  constructor(ctx: Context) { super(ctx, 'greeting-info') }
}

export function greeting(): string { return 'Provider two' }
export function apply(ctx: Context): void {
  if (!(ctx instanceof Context)) throw new Error('Provider received a duplicate Cordis runtime')
  new GreetingInfo(ctx)
  const slots = new SlotCore()
  ctx.effect(() => slots.register({ name: 'root', children: { 'example.panel': { kind: 'list', scope: 'root' } } },
    ({ renderSlot }: PropsRenderSlots<'example.panel'>) => renderSlot('example.panel', {})))
  ctx.provide('slots', slots)
  ctx.provide('greeting', greeting)
}
