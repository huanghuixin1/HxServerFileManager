<script setup>
import { ref, reactive, computed, onMounted, onUnmounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, getToken, setToken, clearToken, setUnauthorizedHandler } from './api.js'
import ConnectPanel from './components/ConnectPanel.vue'
import SavedConnections from './components/SavedConnections.vue'
import FileManager from './components/FileManager.vue'
import Terminal from './components/Terminal.vue'
import LogPanel from './components/LogPanel.vue'
import EditorModal from './components/EditorModal.vue'
import LoginView from './components/LoginView.vue'
import SystemStatus from './components/SystemStatus.vue'

// ---- 登录鉴权状态：探测 /api/session，未认证时显示登录页 ----
const authLoading = ref(true)
const authRequired = ref(false)
const authed = ref(false)

// 多连接：connections 保存所有活跃会话，activeId 标记当前查看的标签
const connections = ref([])
const activeId = ref(null)
const connectVisible = ref(false)
const logEnabled = ref(false) // 实时操作日志默认隐藏，顶部按钮可随时开关
const editor = ref({ open: false, connId: null, path: null })

// ---- 服务器间直传（发送到连接）：选目标连接 + 目标目录 + 进度轮询 ----
const serverCopyVisible = ref(false)
const scpPhase = ref('pick') // pick | copying | done | error
const scpSourceLabel = ref('')
const scpItems = ref([])
const scpTargetConnId = ref(null)
const scpTargetDir = ref('')
const scpJob = ref(null)
let scpJobId = null
let scpTimer = null
// 直传完成后给目标连接的 FileManager 发刷新信号（值 +1 即触发 reload）
const refreshTokens = reactive({})

// 已保存连接（来自后端 connections.json）：下拉快速打开 + 管理/编辑
const savedList = ref([])
const savedReload = ref(0)
const manageVisible = ref(false)
const editing = ref(null)
const editVisible = ref(false)

const activeConn = computed(
  () => connections.value.find((c) => c.connectionId === activeId.value) || null
)

// 可发送的目标连接：排除当前 tab 与连接中的占位 tab，按 host:port:username 去重
const serverCopyTargets = computed(() => {
  const cur = activeConn.value
  if (!cur) return []
  const seen = new Set()
  return connections.value.filter((c) => {
    if (c.pending || c.connectionId === cur.connectionId) return false
    const key = `${c.host}:${c.port}:${c.username}`
    if (seen.has(key)) return false
    seen.add(key)
    return true
  })
})
const hasOtherConns = computed(() => serverCopyTargets.value.length > 0)

// 窄屏（单列布局）时分隔条是横向的，拖拽调高度
const isNarrow = ref(window.matchMedia('(max-width: 1000px)').matches)
window.matchMedia('(max-width: 1000px)').addEventListener('change', (e) => {
  isNarrow.value = e.matches
})

// 文件列表是否跟随终端路径（默认开）；每连接的会话工作目录，终端与文件列表共享
const syncCwd = ref(true)
const cwdMap = reactive({})
// 各连接 Terminal 组件实例（导航时向交互终端注入 cd）
const termRefs = reactive({})

// 工作区布局：终端在左（默认更宽）可拖拽调宽；窄屏单列时终端在上，可拖拽调高度；支持终端最大化
const termMax = ref(false)
const termWidth = ref(58)  // 双列时终端宽度百分比
const termHeight = ref(50) // 单列时终端高度百分比
function startResize(e) {
  if (termMax.value) return
  e.preventDefault() // 防止拖动时选中文本/触发默认行为
  const el = e.currentTarget.parentElement
  const rect = el.getBoundingClientRect()
  // 根据实际布局方向决定拖宽度还是高度（响应式切换时以渲染为准）
  const isCol = getComputedStyle(el).flexDirection === 'column'
  const startPos = isCol ? e.clientY : e.clientX
  const startPct = isCol ? termHeight.value : termWidth.value
  const move = (ev) => {
    const delta = (isCol ? ev.clientY : ev.clientX) - startPos
    const pct = startPct + (delta / (isCol ? rect.height : rect.width)) * 100
    const v = Math.min(75, Math.max(30, pct))
    if (isCol) termHeight.value = v
    else termWidth.value = v
  }
  const up = () => {
    window.removeEventListener('pointermove', move)
    window.removeEventListener('pointerup', up)
    document.body.style.cursor = ''
    document.body.style.userSelect = ''
  }
  window.addEventListener('pointermove', move)
  window.addEventListener('pointerup', up)
  document.body.style.cursor = isCol ? 'row-resize' : 'col-resize'
  document.body.style.userSelect = 'none'
}

// ---- 本地化持久化：打开中的 SSH 会话 + 各自路径，刷新/下次打开自动恢复 ----
const WS_KEY = 'hx_workspace_v1'
let persistTimer = null

async function loadSaved() {
  try {
    const res = await api.listConnections()
    savedList.value = res.connections || []
  } catch (_) { /* 忽略：下拉里会显示空 */ }
}

// 会话探测：决定显示登录页还是主界面；认证通过后才恢复会话/路径
async function checkSession() {
  try {
    const res = await api.session()
    authRequired.value = !!res.required
    authed.value = !!res.authenticated
  } catch (_) {
    // 后端不可达：先按未认证处理，主界面操作时会再报错
    authRequired.value = true
    authed.value = false
  } finally {
    authLoading.value = false
  }
  if (authed.value) {
    loadSaved()
    restoreWorkspace() // 恢复上次打开的 SSH 会话与路径
  }
}

// 登录成功：保存 token 后进入主界面并恢复会话
function onAuthed(res) {
  setToken(res.token, res.remember)
  authed.value = true
  loadSaved()
  restoreWorkspace()
}

// 任意请求 401（token 失效/被吊销）：清 token 退回登录页，并清理界面会话状态
function onUnauthorized() {
  authed.value = false
  connections.value = []
  activeId.value = null
  broken.value = []
  termRefs && Object.keys(termRefs).forEach((k) => delete termRefs[k])
  Object.keys(cwdMap).forEach((k) => delete cwdMap[k])
  // 界面已回登录页，后端会话由空闲回收兜底
}
setUnauthorizedHandler(onUnauthorized)

// 退出登录：吊销 token + 断开所有 SSH 会话 + 回到登录页
async function logout() {
  try {
    await api.logout()
  } catch (_) { /* 忽略：token 可能已失效 */ }
  clearToken()
  // 逐个断开活跃连接，避免退出后仍挂着 SSH 会话
  for (const c of [...connections.value]) {
    try { await api.disconnect(c.connectionId) } catch (_) { /* ignore */ }
  }
  connections.value = []
  activeId.value = null
  broken.value = []
  Object.keys(termRefs).forEach((k) => delete termRefs[k])
  Object.keys(cwdMap).forEach((k) => delete cwdMap[k])
  authed.value = false
}

onMounted(() => {
  checkSession()
})

// ---- SSH 连接断开检测 + 按 R 重连 ----
// 定期轮询后端会话健康接口，发现某活跃连接断开时显示提示条，支持按 R 键重连、Esc 关闭。
const broken = ref([])             // 已断开、等待处理的连接（元素为 connections 里的对象）
const reconnectBusy = ref(false)
let healthTimer = null
const HEALTH_INTERVAL = 15000

function displayName(c) {
  return c.name || `${c.username}@${c.host}:${c.port}`
}

// 轮询会话健康：仅在「之前正常 -> 现在断开」时新增提示，避免重复弹
async function checkSessionHealth() {
  if (!authed.value || connections.value.length === 0) return
  let alive = new Set()
  try {
    const res = await api.sessionsHealth()
    alive = new Set((res.sessions || []).filter((s) => s.connected).map((s) => s.connectionId))
  } catch (_) {
    return // 后端/鉴权异常交给别处处理，这里不打扰
  }
  for (const c of connections.value) {
    if (c.pending) continue // 连接中：尚未建立，跳过健康检查
    if (!alive.has(c.connectionId) && !broken.value.some((b) => b.connectionId === c.connectionId)) {
      broken.value.push(c)
    }
  }
  // broken 中已恢复（重新出现在存活列表/连接已移除）的清除
  broken.value = broken.value.filter(
    (b) => !b.pending && connections.value.some((c) => c.connectionId === b.connectionId) && !alive.has(b.connectionId)
  )
}

// 重连一个断开连接（走已保存的 profileId；无 profile 的只能提示手动重连）
async function doReconnect(conn) {
  if (reconnectBusy.value) return
  if (!conn.profileId) {
    ElMessage.warning(`${displayName(conn)} 未保存为常用连接，无法自动重连，请手动重新连接`)
    return
  }
  reconnectBusy.value = true
  const oldId = conn.connectionId
  const oldCwd = cwdMap[oldId] || conn.homeDirectory || '/'
  try {
    const res = await api.reconnect(conn.profileId)
    const newId = res.connectionId
    const tab = connections.value.find((c) => c.connectionId === oldId)
    if (!tab) {
      // tab 已被用户关闭：按新会话直接新增
      broken.value = broken.value.filter((b) => b.connectionId !== oldId)
      handleConnected({ ...res, port: conn.port, authType: conn.authType }, oldCwd)
      ElMessage.success(`${displayName(conn)} 已重连`)
    } else {
      // 就地重连：不重建 tab。连接对象原地换成新会话 id（uid 不变，工作区不重挂载），
      // 终端组件实例保留，xterm 滚动历史原样显示，只需重建 WebSocket 恢复输入输出
      tab.profileId = res.profileId || tab.profileId
      tab.host = res.host || tab.host
      tab.username = res.username || tab.username
      tab.homeDirectory = res.homeDirectory || tab.homeDirectory
      tab.connectionId = newId
      cwdMap[newId] = oldCwd
      delete cwdMap[oldId]
      if (termRefs[oldId]) {
        termRefs[newId] = termRefs[oldId]
        delete termRefs[oldId]
      }
      if (activeId.value === oldId) activeId.value = newId
      broken.value = broken.value.filter((b) => b.connectionId !== oldId)
      // 同一 xterm 实例上按新 connId 重建 WebSocket（Terminal.reconnect 关旧 ws 后重开）
      termRefs[newId]?.reconnect?.()
      ElMessage.success(`${displayName(tab)} 已重连`)
    }
  } catch (e) {
    // 重连失败：**不删 tab**，连接对象原样保留（含旧 connectionId），
    // 并把连接重新放回 broken，横幅继续提示可按 R 再试
    broken.value = broken.value.filter((b) => b.connectionId !== oldId)
    if (connections.value.some((c) => c.connectionId === oldId) && !broken.value.some((b) => b.connectionId === oldId))
      broken.value.push(conn)
    ElMessage.error(`重连 ${displayName(conn)} 失败：${e.message}`)
  } finally {
    reconnectBusy.value = false
  }
}

// 终端 WebSocket 关闭（SSH 断开/网络异常）：加入 broken 提示，等用户按 R 重连
function onTermDisconnected(conn) {
  if (!conn) return
  if (!connections.value.some((c) => c.connectionId === conn.connectionId)) return
  if (broken.value.some((b) => b.connectionId === conn.connectionId)) return
  broken.value.push(conn)
}

// 键盘：有断开连接时 R 重连第一个、Esc 关闭提示
function onGlobalKeydown(e) {
  if (broken.value.length === 0) return
  const k = e.key.toLowerCase()
  if (k === 'r' && !e.ctrlKey && !e.metaKey && !e.altKey) {
    e.preventDefault()
    doReconnect(broken.value[0])
  } else if (k === 'escape') {
    broken.value = []
  }
}

onMounted(() => {
  healthTimer = setInterval(checkSessionHealth, HEALTH_INTERVAL)
  window.addEventListener('keydown', onGlobalKeydown)
})

onUnmounted(() => {
  if (healthTimer) clearInterval(healthTimer)
  window.removeEventListener('keydown', onGlobalKeydown)
  stopScpPolling()
})

// 把当前活跃会话（profileId + 路径）写入 localStorage（防抖）
function persistWorkspace() {
  if (persistTimer) clearTimeout(persistTimer)
  persistTimer = setTimeout(() => {
    try {
      const sessions = connections.value
        .filter((c) => !c.pending) // 连接中的占位 tab 不持久化
        .map((c) => ({
          profileId: c.profileId || null,
          name: c.name || '',
          host: c.host,
          port: c.port,
          username: c.username,
          cwd: cwdMap[c.connectionId] || '/',
        }))
      localStorage.setItem(WS_KEY, JSON.stringify({ sessions, ts: Date.now() }))
    } catch (_) { /* 忽略存储异常 */ }
  }, 400)
}

// 启动时恢复上次打开的会话与路径（自动重连 + 回到上次目录）
function restoreWorkspace() {
  let saved = null
  try {
    saved = JSON.parse(localStorage.getItem(WS_KEY) || 'null')
  } catch (_) { /* 忽略 */ }
  if (!saved || !Array.isArray(saved.sessions) || saved.sessions.length === 0) return
  for (const s of saved.sessions) {
    if (!s.profileId) continue
    ;(async () => {
      try {
        const res = await api.reconnect(s.profileId)
        handleConnected({ ...res, port: s.port, authType: 'password' }, s.cwd)
      } catch (_) { /* 服务器不可达/连接被删：静默跳过，用户可手动连 */ }
    })()
  }
}

// 连接稳定标识：已保存连接用 profileId，未保存用 username@host:port。
// 收藏/宏按此隔离保存 —— connectionId 每次重连都会变，不能当持久键。
function connKeyOf(c) {
  if (c.profileId) return 'profile:' + c.profileId
  return `${c.username}@${c.host}:${c.port}`
}

function toConn(payload) {
  return {
    // tab 稳定身份：重连成功时 connectionId 会原地换成新会话 id，但 uid 不变，
    // 保证工作区（Terminal/FileManager 组件实例）不因 v-for key 变化而重挂载、终端历史不丢
    uid: Math.random().toString(36).slice(2) + Date.now().toString(36),
    connectionId: payload.connectionId,
    profileId: payload.profileId || null,
    host: payload.host,
    username: payload.username,
    port: payload.port,
    authType: payload.authType,
    name: payload.name || '',
    homeDirectory: payload.homeDirectory || '/',
  }
}

// restoreCwd：可选，恢复上次打开的路径（为空则用家目录）
function handleConnected(payload, restoreCwd) {
  const conn = toConn(payload)
  // 同一 connectionId 去重（如重连同一台）
  const idx = connections.value.findIndex((c) => c.connectionId === conn.connectionId)
  if (idx >= 0) connections.value.splice(idx, 1, conn)
  else connections.value.push(conn)
  activeId.value = conn.connectionId
  connectVisible.value = false
  editor.value = { open: false, connId: null, path: null }
  cwdMap[conn.connectionId] = restoreCwd || conn.homeDirectory || '/'
  loadSaved() // 刷新排序/别名
  persistWorkspace()
}

async function disconnectConn(connId) {
  try {
    await api.disconnect(connId)
  } catch (_) { /* 忽略：会话可能已被后端回收 */ }
  // 断开时 Terminal 的 ws close 会在 tab 移除前触发 onTermDisconnected，
  // 这里把该连接从 broken 中一并清掉，避免手动关闭后仍弹「连接已断开」
  broken.value = broken.value.filter((b) => b.connectionId !== connId)
  const idx = connections.value.findIndex((c) => c.connectionId === connId)
  if (idx >= 0) connections.value.splice(idx, 1)
  delete cwdMap[connId]
  if (activeId.value === connId) {
    const rest = connections.value
    activeId.value = rest.length ? rest[rest.length - 1].connectionId : null
  }
  persistWorkspace()
}

// 文件列表导航 -> 同步会话 cwd（后端 exec 链路），并让交互终端执行 cd（双向联动）。
// 「同步路径」关闭时两边完全独立：文件列表只管自己，不动终端任何状态。
async function onNavigate(connId, path) {
  if (!path) return
  if (!syncCwd.value) return
  cwdMap[connId] = path
  try {
    await api.setCwd(connId, path)
  } catch (_) { /* 目录可能不存在，忽略 */ }
  // 交互终端里直接 cd（组件内部判断是否交互模式）
  termRefs[connId]?.injectCd?.(path)
  persistWorkspace()
}

// 交互终端 cd（OSC 7 推送）-> 更新共享 cwd，文件列表跟随；并同步后端 exec 链路目录
function onCwdChanged(connId, path) {
  if (!path || !syncCwd.value) return
  cwdMap[connId] = path
  api.setCwd(connId, path).catch(() => {})
  persistWorkspace()
}

async function disconnectActive() {
  if (!activeConn.value) return
  if (activeConn.value.pending) {
    // 连接中的占位 tab：仅关闭标签
    connections.value = connections.value.filter((c) => c.uid !== activeConn.value.uid)
    delete cwdMap[activeId.value]
    const rest = connections.value
    activeId.value = rest.length ? rest[rest.length - 1].connectionId : null
    persistWorkspace()
    return
  }
  await disconnectConn(activeId.value)
}

async function onTabRemove(name) {
  const conn = connections.value.find((c) => c.connectionId === name)
  if (!conn) return
  if (conn.pending) {
    // 连接中的占位 tab：直接移除，后端会话仍会建立，由空闲回收兜底
    connections.value = connections.value.filter((c) => c.uid !== conn.uid)
    delete cwdMap[name]
    if (activeId.value === name) {
      const rest = connections.value
      activeId.value = rest.length ? rest[rest.length - 1].connectionId : null
    }
    persistWorkspace()
    return
  }
  try {
    await ElMessageBox.confirm(
      `断开连接 “${conn.name || `${conn.username}@${conn.host}:${conn.port}`}” ？`,
      '断开确认',
      { type: 'warning', confirmButtonText: '断开', cancelButtonText: '取消' }
    )
  } catch (_) {
    return // 用户取消
  }
  await disconnectConn(name)
}

// 用已保存的连接快速打开（无需重新输入）。
// 连接较慢时先占位开一个「正在连接」tab，成功后再原地填充真实会话（uid 不变，不重挂载工作区）。
async function openSaved(p) {
  const uid = Math.random().toString(36).slice(2) + Date.now().toString(36)
  const placeholder = {
    ...toConn({
      connectionId: 'pending-' + uid,
      profileId: p.id,
      host: p.host,
      username: p.username,
      port: p.port,
      authType: p.authType,
      name: p.name || '',
    }),
    uid,
    pending: true,
  }
  connections.value.push(placeholder)
  activeId.value = placeholder.connectionId
  cwdMap[placeholder.connectionId] = '/'
  try {
    const res = await api.reconnect(p.id)
    const idx = connections.value.findIndex((c) => c.uid === uid)
    if (idx < 0) return // tab 已被用户关闭，会话交由后端空闲回收
    const conn = toConn({
      ...res,
      port: p.port,
      authType: p.authType,
      name: res.name || p.name || '',
    })
    conn.uid = uid // 保留 uid：工作区（Terminal/FileManager）不重挂载
    const oldId = placeholder.connectionId
    delete cwdMap[oldId]
    cwdMap[conn.connectionId] = conn.homeDirectory || '/'
    if (termRefs[oldId]) {
      termRefs[conn.connectionId] = termRefs[oldId]
      delete termRefs[oldId]
    }
    connections.value.splice(idx, 1, conn)
    if (activeId.value === oldId) activeId.value = conn.connectionId
    loadSaved() // 刷新排序/别名
    persistWorkspace()
  } catch (e) {
    const idx = connections.value.findIndex((c) => c.uid === uid)
    if (idx >= 0) connections.value.splice(idx, 1)
    delete cwdMap[placeholder.connectionId]
    if (activeId.value === placeholder.connectionId) {
      const rest = connections.value
      activeId.value = rest.length ? rest[rest.length - 1].connectionId : null
    }
    ElMessage.error(`连接 ${p.name || `${p.username}@${p.host}:${p.port}`} 失败：${e.message}`)
  }
}

function onSavedCommand(cmd) {
  if (cmd === 'manage') {
    manageVisible.value = true
    return
  }
  if (cmd.startsWith('open:')) {
    const p = savedList.value.find((x) => x.id === cmd.slice(5))
    if (p) openSaved(p)
  }
}

function openEdit(profile) {
  editing.value = profile
  editVisible.value = true
}

function onUpdated() {
  editVisible.value = false
  editing.value = null
  savedReload.value++ // 刷新管理面板/空态面板
  loadSaved()         // 刷新下拉
  ElMessage.success('已保存修改')
}

function openEditor(connId, path) {
  editor.value = { open: true, connId, path }
}
function closeEditor() {
  editor.value = { open: false, connId: null, path: null }
}

// ---- 服务器间直传（发送到连接）----
function parentDirOf(p) {
  p = (p || '/').replace(/\/+$/, '')
  if (p === '' || p === '/') return '/'
  const i = p.lastIndexOf('/')
  return i <= 0 ? '/' : p.slice(0, i)
}

// FileManager 发来选中项路径：打开「选择目标连接」对话框，默认目标目录 = 选中项所在目录
function onSendToConnection(paths) {
  const cur = activeConn.value
  if (!cur || !paths || !paths.length) return
  scpSourceLabel.value = cur.name || `${cur.username}@${cur.host}:${cur.port}`
  scpItems.value = paths
  scpTargetConnId.value = null
  scpTargetDir.value = parentDirOf(paths[0])
  scpJob.value = null
  scpPhase.value = 'pick'
  serverCopyVisible.value = true
}

function stopScpPolling() {
  if (scpTimer) {
    clearInterval(scpTimer)
    scpTimer = null
  }
}

function resetServerCopy() {
  stopScpPolling()
  scpJobId = null
}

async function startServerCopy() {
  const cur = activeConn.value
  if (!cur || !scpTargetConnId.value) return
  const targetDir = scpTargetDir.value.trim()
  if (!targetDir.startsWith('/')) {
    ElMessage.warning('目标目录必须是绝对路径（以 / 开头）')
    return
  }
  scpPhase.value = 'copying'
  scpJob.value = null
  try {
    const res = await api.serverCopy({
      sourceConnId: cur.connectionId,
      targetConnId: scpTargetConnId.value,
      items: scpItems.value,
      targetDir,
    })
    scpJobId = res.jobId
    scpTimer = setInterval(pollServerCopy, 1200)
    pollServerCopy() // 立即查一次
  } catch (e) {
    scpPhase.value = 'error'
    scpJob.value = { state: 'failed', error: e.message, items: [], done: 0, total: scpItems.value.length }
    ElMessage.error(e.message)
  }
}

async function pollServerCopy() {
  if (!scpJobId) return
  try {
    const st = await api.serverCopyStatus(scpJobId)
    scpJob.value = st
    if (st.state === 'done') {
      stopScpPolling()
      scpPhase.value = 'done'
      // 目标 tab 在应用里打开着：通知它的文件列表刷新
      const tid = scpTargetConnId.value
      if (tid) refreshTokens[tid] = (refreshTokens[tid] || 0) + 1
      ElMessage.success(`已发送 ${st.done}/${st.total} 项到 ${st.target}:${st.targetDir}`)
    } else if (st.state === 'failed') {
      stopScpPolling()
      scpPhase.value = 'error'
      ElMessage.error(`发送失败：${st.error || '未知错误'}`)
    }
  } catch (e) {
    // 任务可能已过期（长时间没轮询）或后端不可达
    stopScpPolling()
    scpPhase.value = 'error'
    ElMessage.error(e.message)
  }
}
</script>

<template>
  <div v-if="authLoading" class="auth-loading">
    <el-icon class="is-loading" :size="26"><Loading /></el-icon>
  </div>
  <LoginView v-else-if="authRequired && !authed" @authed="onAuthed" />
  <div v-else class="app">
    <header class="topbar">
      <div class="brand">
        <el-icon :size="18"><Monitor /></el-icon>
        <span>HxServerFileManager</span>
      </div>
      <el-tag
        class="status-tag"
        :type="activeConn ? 'success' : 'info'"
        size="small"
        effect="light"
        round
      >
        <span class="dot" :class="{ on: activeConn }"></span>
        {{ activeConn ? `${activeConn.username}@${activeConn.host}:${activeConn.port}` : '未连接' }}
      </el-tag>
      <div class="actions">
        <el-button text @click="logEnabled = !logEnabled">
          {{ logEnabled ? '隐藏日志' : '显示日志' }}
        </el-button>

        <!-- 已保存连接：连接中也可一键再开一个 -->
        <el-dropdown trigger="click" @command="onSavedCommand">
          <el-button plain>
            <el-icon style="margin-right: 4px"><Connection /></el-icon>已保存连接
            <el-icon class="el-icon--right"><ArrowDown /></el-icon>
          </el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item v-if="!savedList.length" disabled>
                暂无已保存的连接
              </el-dropdown-item>
              <el-dropdown-item
                v-for="c in savedList"
                :key="c.id"
                :command="'open:' + c.id"
              >
                <span class="dd-name">{{ c.name }}</span>
                <span class="dd-sub">{{ c.username }}@{{ c.host }}:{{ c.port }}</span>
              </el-dropdown-item>
              <el-dropdown-item v-if="savedList.length" divided command="manage">
                <el-icon><Setting /></el-icon> 管理已保存的连接…
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>

        <el-button type="primary" plain @click="connectVisible = true">
          <el-icon style="margin-right: 4px"><Plus /></el-icon>新建连接
        </el-button>
        <el-button v-if="activeConn" type="danger" plain @click="disconnectActive">
          断开当前
        </el-button>
        <el-button v-if="authRequired" text type="warning" @click="logout">
          <el-icon style="margin-right: 4px"><SwitchButton /></el-icon>退出登录
        </el-button>
      </div>
    </header>

    <!-- 多连接标签栏：每个活跃会话一个标签，可关闭；有别名时显示别名 -->
    <div v-if="connections.length > 0" class="tabsbar">
      <div
        v-for="c in connections"
        :key="c.connectionId"
        class="sess-tab"
        :class="{ active: activeId === c.connectionId }"
        role="tab"
        :aria-selected="activeId === c.connectionId"
        @click="activeId = c.connectionId"
      >
        <el-icon
          v-if="c.pending"
          class="t-loading is-loading"
          :size="12"
        ><Loading /></el-icon>
        <span v-else class="t-dot" :class="{ on: activeId === c.connectionId }"></span>
        <span class="t-label" :title="`${c.username}@${c.host}:${c.port}`">
          {{ c.name || `${c.username}@${c.host}:${c.port}` }}
        </span>
        <el-icon
          class="t-close"
          title="断开该连接"
          @click.stop="onTabRemove(c.connectionId)"
        ><Close /></el-icon>
      </div>
    </div>

    <main class="content">
      <!-- 无任何连接：内联连接表单 + 已保存连接 -->
      <section v-if="connections.length === 0" class="connect-area">
        <ConnectPanel @connected="handleConnected" />
        <SavedConnections
          :reload-token="savedReload"
          @reconnect="handleConnected"
          @edit="openEdit"
        />
      </section>
      <!-- 有连接：按标签渲染工作区（v-show 保留每会话的终端历史/目录状态） -->
      <section v-else class="sessions">
        <div
          v-for="c in connections"
          v-show="activeId === c.connectionId"
          :key="c.uid"
          class="workspace"
          :class="{ 'term-max': termMax }"
          :style="{ '--term-w': termWidth + '%', '--term-h': termHeight + '%' }"
        >
          <!-- 连接中：占位提示，连接完成后才渲染终端/文件列表 -->
          <div v-if="c.pending" class="conn-pending">
            <el-icon class="is-loading" :size="28"><Loading /></el-icon>
            <span>正在连接 {{ c.name || `${c.username}@${c.host}:${c.port}` }}…</span>
          </div>
          <template v-else>
            <!-- 终端在左（默认更宽），文件列表在右（可窄） -->
            <Terminal
              :ref="(el) => { if (el) termRefs[c.connectionId] = el }"
              :conn-id="c.connectionId"
              :conn-key="connKeyOf(c)"
              :cwd="cwdMap[c.connectionId]"
              :maximized="termMax"
              @update:cwd="(p) => onCwdChanged(c.connectionId, p)"
              @toggle-max="termMax = !termMax"
              @disconnected="onTermDisconnected(c)"
            />
            <div
              v-if="!termMax"
              class="ws-divider"
              :class="{ narrow: isNarrow }"
              :title="isNarrow ? '按住上下拖拽调整高度' : '按住左右拖拽调整宽度'"
              @pointerdown="startResize"
            ></div>
            <FileManager
              :conn-id="c.connectionId"
              :conn-key="connKeyOf(c)"
              :initial-dir="c.homeDirectory"
              :sync-cwd="syncCwd"
              :external-path="syncCwd ? cwdMap[c.connectionId] : null"
              :has-other-conns="hasOtherConns"
              :refresh-token="refreshTokens[c.connectionId] || 0"
              @open-file="(p) => openEditor(c.connectionId, p)"
              @navigate="(p) => onNavigate(c.connectionId, p)"
              @update:sync-cwd="(v) => (syncCwd = v)"
              @send-to-connection="onSendToConnection"
            />
          </template>
        </div>
      </section>
    </main>

    <LogPanel v-if="logEnabled" />

    <!-- SSH 连接断开提示：按 R 重连，Esc 关闭 -->
    <transition name="slide-down">
      <div v-if="broken.length" class="broken-banner">
        <el-icon class="warn"><WarningFilled /></el-icon>
        <span class="txt">
          连接已断开：<b>{{ broken.map(displayName).join('、') }}</b> — 按 <b class="key">R</b> 重连，
          <b class="key">Esc</b> 关闭
        </span>
        <el-button size="small" type="warning" :loading="reconnectBusy" @click="doReconnect(broken[0])">
          重连
        </el-button>
        <el-icon class="close" title="关闭" @click="broken = []"><Close /></el-icon>
      </div>
    </transition>

    <!-- 服务器状态：底部迷你状态栏（常驻）+ 点“详情”弹窗（连接中不显示） -->
    <SystemStatus v-if="activeConn && !activeConn.pending" :conn-id="activeId" />

    <!-- 新建连接对话框（连接中也可随时打开） -->
    <el-dialog
      v-model="connectVisible"
      title="连接新服务器"
      width="min(480px, 92vw)"
      :close-on-click-modal="false"
    >
      <ConnectPanel @connected="handleConnected" />
    </el-dialog>

    <!-- 管理已保存的连接（连接中也可进入） -->
    <el-dialog
      v-model="manageVisible"
      title="管理已保存的连接"
      width="min(560px, 94vw)"
      :close-on-click-modal="false"
    >
      <SavedConnections
        :reload-token="savedReload"
        @reconnect="handleConnected"
        @edit="openEdit"
      />
    </el-dialog>

    <!-- 编辑已保存的连接（可改别名/主机/凭据） -->
    <el-dialog
      v-model="editVisible"
      title="编辑已保存的连接"
      width="min(480px, 92vw)"
      :close-on-click-modal="false"
    >
      <ConnectPanel mode="edit" :initial="editing" @updated="onUpdated" />
    </el-dialog>

    <EditorModal
      v-if="editor.open"
      :conn-id="editor.connId"
      :path="editor.path"
      @close="closeEditor"
    />

    <!-- 服务器间直传：选择目标连接 + 目标目录 + 传输进度（不经本机中转） -->
    <el-dialog
      v-model="serverCopyVisible"
      :title="scpPhase === 'pick' ? '发送到连接' : '发送进度'"
      width="min(560px, 94vw)"
      :close-on-click-modal="false"
      @closed="resetServerCopy"
    >
      <div v-if="scpPhase === 'pick'" class="scp-pick">
        <div class="scp-line">
          源：<b>{{ scpSourceLabel }}</b>，已选 {{ scpItems.length }} 项
        </div>
        <div class="scp-label">选择目标连接（{{ serverCopyTargets.length }} 个可用）</div>
        <el-radio-group v-model="scpTargetConnId" class="scp-radios">
          <el-radio
            v-for="t in serverCopyTargets"
            :key="t.connectionId"
            :value="t.connectionId"
            class="scp-radio"
          >
            <span class="scp-radio-name">{{ t.name || `${t.username}@${t.host}:${t.port}` }}</span>
            <span class="scp-radio-sub">{{ t.username }}@{{ t.host }}:{{ t.port }}</span>
          </el-radio>
        </el-radio-group>
        <div v-if="!serverCopyTargets.length" class="scp-none">
          没有其他活跃连接，请先再打开一个连接
        </div>
        <div class="scp-label">目标目录（在目标服务器上）</div>
        <el-input
          v-model="scpTargetDir"
          placeholder="/绝对路径，默认与源选中项所在目录相同"
          @keyup.enter="startServerCopy"
        />
      </div>

      <div v-else class="scp-progress">
        <div class="scp-line">
          {{ scpJob && scpJob.state === 'failed' ? '发送失败' : `正在发送 ${scpJob ? scpJob.done : 0}/${scpJob ? scpJob.total : 0}` }}
        </div>
        <el-progress
          v-if="scpJob && scpJob.total"
          :percentage="Math.round(((scpJob.done || 0) / scpJob.total) * 100)"
          :stroke-width="10"
          :status="scpJob.state === 'failed' ? 'exception' : scpJob.state === 'done' ? 'success' : undefined"
        />
        <div class="scp-items">
          <div
            v-for="it in (scpJob ? scpJob.items : [])"
            :key="it.path"
            class="scp-item"
            :class="it.state"
          >
            <el-icon class="scp-ico">
              <SuccessFilled v-if="it.state === 'done'" />
              <Loading v-else-if="it.state === 'running'" class="is-loading" />
              <CircleCloseFilled v-else-if="it.state === 'failed'" />
              <Clock v-else />
            </el-icon>
            <span class="scp-item-path" :title="it.path">{{ it.path }}</span>
            <span v-if="it.state === 'failed' && it.message" class="scp-item-msg">{{ it.message }}</span>
          </div>
        </div>
        <el-alert
          v-if="scpJob && scpJob.error"
          :title="scpJob.error"
          type="error"
          :closable="false"
          show-icon
          class="scp-alert"
        />
      </div>

      <template #footer>
        <el-button @click="serverCopyVisible = false">
          {{ scpPhase === 'copying' ? '后台运行' : '关闭' }}
        </el-button>
        <el-button
          v-if="scpPhase === 'pick'"
          type="primary"
          :disabled="!scpTargetConnId"
          @click="startServerCopy"
        >
          开始发送
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style>
.auth-loading {
  height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #2d6cdf;
}
.app {
  display: flex;
  flex-direction: column;
  height: 100vh;
  overflow: hidden;
}
.topbar {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 8px 18px;
  background: #fff;
  border-bottom: 1px solid var(--border);
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.04);
  z-index: 5;
  flex-wrap: wrap;
}
.brand {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 700;
  font-size: 16px;
  color: #1f2d3d;
}
.status-tag {
  font-size: 13px;
}
.dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: #c0c8d0;
  margin-right: 6px;
  vertical-align: 1px;
}
.dot.on {
  background: #2ecc71;
}
.actions {
  margin-left: auto;
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  align-items: center;
}
.dd-name {
  font-weight: 600;
  margin-right: 8px;
}
.dd-sub {
  color: #8a97a5;
  font-size: 12px;
}
.tabsbar {
  background: #fff;
  border-bottom: 1px solid var(--border);
  padding: 6px 14px 0;
  flex-shrink: 0;
  display: flex;
  align-items: flex-end;
  gap: 6px;
  overflow-x: auto;
}
.sess-tab {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  padding: 8px 10px 8px 12px;
  border: 1px solid var(--border);
  border-bottom: none;
  border-radius: 9px 9px 0 0;
  background: #f7f9fc;
  color: #5b6b7b;
  font-size: 13px;
  cursor: pointer;
  white-space: nowrap;
  flex-shrink: 0;
  transition: background 0.15s, color 0.15s;
  user-select: none;
}
.sess-tab:hover {
  background: #eef3fa;
}
.sess-tab.active {
  background: #fff;
  color: #1f2d3d;
  font-weight: 600;
  border-color: var(--border);
  box-shadow: 0 -2px 6px rgba(45, 108, 223, 0.06);
}
.t-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: #c0c8d0;
  flex-shrink: 0;
}
.t-loading {
  color: #2d6cdf;
  flex-shrink: 0;
}
.t-dot.on {
  background: #2ecc71;
}
.t-close {
  font-size: 13px;
  border-radius: 50%;
  padding: 2px;
  color: #9aa7b5;
}
.t-close:hover {
  color: #c0392b;
  background: #fdecec;
}
.content {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 18px;
}
.connect-area {
  display: grid;
  grid-template-columns: 380px 1fr;
  gap: 18px;
  align-items: start;
  max-width: 1100px;
  margin: 0 auto;
}
.sessions {
  height: 100%;
  min-height: 0;
}
.workspace {
  display: flex;
  height: 100%;
  min-height: 0;
}
.conn-pending {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  color: #2d6cdf;
  font-size: 14px;
  background: #fafcff;
  border: 1px dashed #c9d6e8;
  border-radius: 10px;
}
.workspace > .term {
  /* 终端在左，默认 58%，拖拽调宽（flex-basis 由 --term-w 控制） */
  flex: 0 0 var(--term-w, 58%);
  min-width: 0;
}
.workspace > .fm {
  flex: 1 1 0;
  min-width: 0;
}
.ws-divider {
  flex-shrink: 0;
  width: 20px; /* 拖拽热区加宽，更容易抓住 */
  cursor: col-resize;
  display: flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  transition: background 0.15s;
}
.ws-divider:hover {
  background: rgba(45, 108, 223, 0.08); /* hover 整条高亮，提示可拖 */
}
.ws-divider::after {
  content: '';
  width: 4px;
  height: 64px;
  border-radius: 3px;
  background: #d8e0ea;
  transition: background 0.15s;
}
.ws-divider:hover::after {
  background: #2d6cdf;
}
/* 终端最大化：占满整个工作区，隐藏文件列表与分隔条 */
.workspace.term-max > .term {
  flex: 1 0 100%;
}
.workspace.term-max > .fm,
.workspace.term-max > .ws-divider {
  display: none;
}
@media (max-width: 1000px) {
  .connect-area {
    grid-template-columns: 1fr;
  }
  .workspace {
    /* 单列上下排：终端在上，文件列表在下，各占一半 */
    flex-direction: column;
  }
  .workspace > .term {
    flex: 0 0 var(--term-h, 50%); /* 单列：终端在上，可拖拽调高度 */
  }
  .ws-divider {
    width: 100%;
    height: 20px; /* 横条也加宽 */
    cursor: row-resize;
    flex-shrink: 0;
  }
  .ws-divider::after {
    height: 4px;
    width: 64px;
  }
  .workspace.term-max > .term {
    flex: 1 0 100%;
  }
}
/* SSH 连接断开提示条 */
.broken-banner {
  position: fixed;
  top: 12px;
  left: 50%;
  transform: translateX(-50%);
  z-index: 2001;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 14px;
  background: #fffbe6;
  border: 1px solid #ffe58f;
  border-radius: 10px;
  box-shadow: 0 6px 20px rgba(0, 0, 0, 0.12);
  color: #7a5c00;
  font-size: 13.5px;
  max-width: 90vw;
}
.broken-banner .warn {
  color: #faad14;
  font-size: 18px;
}
.broken-banner .txt {
  flex: 1;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.broken-banner b.key {
  display: inline-block;
  min-width: 18px;
  text-align: center;
  padding: 0 5px;
  margin: 0 2px;
  border: 1px solid #d9c36a;
  border-bottom-width: 2px;
  border-radius: 4px;
  background: #fff;
  color: #7a5c00;
  font-size: 12px;
}
.broken-banner .close {
  cursor: pointer;
  color: #b9a34d;
  font-size: 15px;
}
.broken-banner .close:hover {
  color: #7a5c00;
}
.slide-down-enter-active,
.slide-down-leave-active {
  transition: all 0.25s ease;
}
.slide-down-enter-from,
.slide-down-leave-to {
  opacity: 0;
  transform: translateX(-50%) translateY(-12px);
}

/* ---- 服务器间直传对话框 ---- */
.scp-pick {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.scp-line {
  font-size: 13.5px;
  color: #2a3542;
}
.scp-line b {
  color: #1f2d3d;
}
.scp-label {
  font-size: 13px;
  color: #5b6b7b;
  margin-top: 8px;
}
.scp-radios {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 4px;
}
.scp-radio {
  height: auto;
  margin-right: 0;
  padding: 6px 10px;
  border: 1px solid var(--border);
  border-radius: 8px;
  transition: border-color 0.15s;
}
.scp-radio:hover {
  border-color: #b9cbe8;
}
.scp-radio.is-active {
  border-color: #2d6cdf;
}
.scp-radio .el-radio__label {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding-left: 8px;
}
.scp-radio-name {
  font-size: 13.5px;
  font-weight: 600;
  color: #2a3542;
}
.scp-radio-sub {
  font-size: 12px;
  color: #8a97a5;
}
.scp-none {
  font-size: 13px;
  color: #c0392b;
  padding: 6px 0;
}
.scp-progress .el-progress {
  margin-top: 4px;
}
.scp-items {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-height: 260px;
  overflow: auto;
  margin-top: 12px;
}
.scp-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  font-size: 13px;
  padding: 4px 6px;
  border-radius: 6px;
}
.scp-item .scp-ico {
  flex-shrink: 0;
  margin-top: 2px;
  color: #b6c0cc;
}
.scp-item.done .scp-ico {
  color: #2ecc71;
}
.scp-item.running .scp-ico {
  color: #2d6cdf;
}
.scp-item.failed {
  background: #fdecec;
}
.scp-item.failed .scp-ico {
  color: #c0392b;
}
.scp-item-path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: ui-monospace, monospace;
  color: #2a3542;
}
.scp-item-msg {
  color: #c0392b;
  font-size: 12px;
  max-width: 45%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.scp-alert {
  margin-top: 12px;
}
</style>
