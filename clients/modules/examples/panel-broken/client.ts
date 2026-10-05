import type { Context } from '@deepseek-ai/cordis'
import { greeting } from 'example-provider/client'

export const inject = ['greeting']
export function apply(ctx: Context): void {
  ctx.effect(() => {
    const element = document.createElement('div')
    element.id = 'failed-contribution'
    element.textContent = greeting()
    document.querySelector('#contributions')?.append(element)
    return () => { element.remove() }
  })
  throw new Error('Example panel activation failed')
}
