<script setup>
import { ref, reactive, computed, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, getToken, setToken, clearToken, setUnauthorizedHandler } from './api.js'
import ConnectPanel from './components/ConnectPanel.vue'
import SavedConnections from './components/SavedConnections.vue'
import FileManager from './components/FileManager.vue'
import Terminal from './components/Terminal.vue'
import LogPanel from './components/LogPanel.vue'
import EditorModal from './components/EditorModal.vue'
import LoginView from './components/LoginView.vue'

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

// 已保存连接（来自后端 connections.json）：下拉快速打开 + 管理/编辑
const savedList = ref([])
const savedReload = ref(0)
const manageVisible = ref(false)
const editing = ref(null)
const editVisible = ref(false)

const activeConn = computed(
  () => connections.value.find((c) => c.connectionId === activeId.value) || null
)

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
  Object.keys(termRefs).forEach((k) => delete termRefs[k])
  Object.keys(cwdMap).forEach((k) => delete cwdMap[k])
  authed.value = false
}

onMounted(() => {
  checkSession()
})

// 把当前活跃会话（profileId + 路径）写入 localStorage（防抖）
function persistWorkspace() {
  if (persistTimer) clearTimeout(persistTimer)
  persistTimer = setTimeout(() => {
    try {
      const sessions = connections.value.map((c) => ({
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

function toConn(payload) {
  return {
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
  await disconnectConn(activeId.value)
}

async function onTabRemove(name) {
  const conn = connections.value.find((c) => c.connectionId === name)
  if (!conn) return
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

// 用已保存的连接快速打开（无需重新输入）
async function openSaved(p) {
  try {
    const res = await api.reconnect(p.id)
    handleConnected({ ...res, port: p.port, authType: p.authType })
  } catch (e) {
    ElMessage.error(e.message)
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
        <span class="t-dot" :class="{ on: activeId === c.connectionId }"></span>
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
          :key="c.connectionId"
          class="workspace"
          :class="{ 'term-max': termMax }"
          :style="{ '--term-w': termWidth + '%', '--term-h': termHeight + '%' }"
        >
          <!-- 终端在左（默认更宽），文件列表在右（可窄） -->
          <Terminal
            :ref="(el) => { if (el) termRefs[c.connectionId] = el }"
            :conn-id="c.connectionId"
            :cwd="cwdMap[c.connectionId]"
            :maximized="termMax"
            @update:cwd="(p) => onCwdChanged(c.connectionId, p)"
            @toggle-max="termMax = !termMax"
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
            :initial-dir="c.homeDirectory"
            :sync-cwd="syncCwd"
            :external-path="syncCwd ? cwdMap[c.connectionId] : null"
            @open-file="(p) => openEditor(c.connectionId, p)"
            @navigate="(p) => onNavigate(c.connectionId, p)"
            @update:sync-cwd="(v) => (syncCwd = v)"
          />
        </div>
      </section>
    </main>

    <LogPanel v-if="logEnabled" />

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
</style>
