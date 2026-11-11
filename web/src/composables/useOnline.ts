import { onBeforeUnmount, onMounted, ref, type Ref } from 'vue'

/**
 * Whether the browser says it has a network, kept up to date while the component is mounted. `onChange` runs after
 * each change. This is the browser's word only: the server may still be out of reach (`auth.offline` says that).
 */
export function useOnline(onChange?: (online: boolean) => void): Readonly<Ref<boolean>> {
  const online = ref(navigator.onLine)
  const update = () => {
    online.value = navigator.onLine
    onChange?.(online.value)
  }
  onMounted(() => {
    window.addEventListener('online', update)
    window.addEventListener('offline', update)
  })
  onBeforeUnmount(() => {
    window.removeEventListener('online', update)
    window.removeEventListener('offline', update)
  })
  return online
}
