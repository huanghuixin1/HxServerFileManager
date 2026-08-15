import { ref } from 'vue'
import { api } from './api.js'

// 全局单例偏好设置：FileManager（收藏目录）与 Terminal（宏）共享同一份数据，
// 模块级状态 + 幂等的 ensureLoaded，任意组件挂载时调用一次即可。改动后调用对应 save*。
const favorites = ref([]) // FavoriteDir[]：{id, connectionId, name, path, createdAt, updatedAt}
const macros = ref([]) // TerminalMacro[]：{id, name, command, createdAt, updatedAt}

let loadedOnce = false

async function ensureLoaded() {
  if (loadedOnce) return
  const [fav, mac] = await Promise.all([
    api.getFavorites().catch(() => ({ favorites: [] })),
    api.getMacros().catch(() => ({ macros: [] })),
  ])
  favorites.value = fav.favorites || []
  macros.value = mac.macros || []
  loadedOnce = true
}

function newId() {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) return crypto.randomUUID()
  return 'id-' + Date.now().toString(36) + Math.random().toString(36).slice(2, 8)
}

export function useSettings() {
  return {
    favorites,
    macros,
    ensureLoaded,
    newId,
    saveFavorites: async () => {
      await api.putFavorites(favorites.value)
    },
    saveMacros: async () => {
      await api.putMacros(macros.value)
    },
  }
}