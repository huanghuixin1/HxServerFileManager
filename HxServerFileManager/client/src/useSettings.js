import { ref } from 'vue'
import { api } from './api.js'

// 全局单例偏好设置：FileManager（收藏目录）、Terminal（宏 + 命令历史）共享同一份数据，
// 模块级状态 + 幂等的 ensureLoaded，任意组件挂载时调用一次即可。改动后调用对应 save*。
const favorites = ref([]) // FavoriteDir[]：{id, connectionId, name, path, createdAt, updatedAt}
const macros = ref([]) // TerminalMacro[]：{id, name, command, createdAt, updatedAt}
// CommandHistoryItem[]：{connKey, command, cwd, exitStatus, createdAt}，按 connKey 隔离
const history = ref([])
// 全局代理：{type, host, port, username, password} | null（连接级「跟随全局」时使用）
const proxy = ref(null)

// 每个连接最多保留的命令历史条数（与后端 SettingsStore.AppendHistory 的 200 一致）
const HISTORY_LIMIT = 200

let loadedOnce = false

async function ensureLoaded() {
  if (loadedOnce) return
  const [fav, mac, hist, px] = await Promise.all([
    api.getFavorites().catch(() => ({ favorites: [] })),
    api.getMacros().catch(() => ({ macros: [] })),
    api.getHistory().catch(() => ({ history: [] })),
    api.getProxy().catch(() => ({ proxy: null })),
  ])
  favorites.value = fav.favorites || []
  macros.value = mac.macros || []
  history.value = hist.history || []
  proxy.value = px.proxy || null
  loadedOnce = true
}

function newId() {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) return crypto.randomUUID()
  return 'id-' + Date.now().toString(36) + Math.random().toString(36).slice(2, 8)
}

// 记录一条已执行的命令（快捷命令回车 / 交互终端按回车执行）。
// 本地先行更新（UI 即时可见），再 POST 到后端持久化；失败静默（下次 GET 会以服务端为准修正）。
function addHistory(connKey, command, cwd, exitStatus) {
  const cmd = (command || '').trim()
  if (!connKey || !cmd) return
  const item = {
    connKey,
    command: cmd,
    cwd: cwd || '',
    exitStatus: exitStatus ?? -1,
    createdAt: new Date().toISOString(),
  }
  // 同一连接同一命令只留最新一条（时间戳刷新置顶，类似 shell history 的重复执行）
  const others = history.value.filter((h) => !(h.connKey === connKey && h.command === cmd))
  history.value = [...others, item]
  // 每连接上限：超过丢最旧
  const perKey = history.value.filter((h) => h.connKey === connKey)
  if (perKey.length > HISTORY_LIMIT) {
    const drop = new Set(perKey.slice(0, perKey.length - HISTORY_LIMIT))
    history.value = history.value.filter((h) => !(h.connKey === connKey && drop.has(h)))
  }
  api.addHistory(item).catch(() => {})
}

// 清空某连接的命令历史（本地立即清，DELETE 后端失败时下次加载会带回旧数据）
function clearHistory(connKey) {
  history.value = history.value.filter((h) => h.connKey !== connKey)
  api.clearHistory(connKey).catch(() => {})
}

// 已保存连接的代理展示信息：follow → 跟随全局（title 带全局当前配置），custom → 自定义。
// 返回 null = 直连/未配置代理，调用方不展示标签。
function proxyTagInfo(c) {
  if (!c) return null
  if (c.proxyMode === 'custom') {
    const p = c.proxy || {}
    const t = String(p.type || 'http').toUpperCase()
    return { text: '自定义代理', title: `自定义代理：${t} ${p.host || '?'}:${p.port || '?'}` }
  }
  if (c.proxyMode === 'follow') {
    const g = proxy.value
    const t = String(g?.type || 'http').toUpperCase()
    return g?.host
      ? { text: '全局代理', title: `跟随全局代理：${t} ${g.host}:${g.port || ''}` }
      : { text: '全局代理', title: '跟随全局代理（全局未配置，实际直连）' }
  }
  return null
}

export function useSettings() {
  return {
    favorites,
    macros,
    history,
    proxy,
    ensureLoaded,
    newId,
    addHistory,
    clearHistory,
    proxyTagInfo,
    saveFavorites: async () => {
      await api.putFavorites(favorites.value)
    },
    saveMacros: async () => {
      await api.putMacros(macros.value)
    },
    // 保存全局代理（先改 proxy.value 再调用；host 清空即整体置 null 停用）
    saveProxy: async () => {
      await api.putProxy(proxy.value)
    },
  }
}
