<script setup>
import { ref, computed, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api } from './api.js'
import ConnectPanel from './components/ConnectPanel.vue'
import SavedConnections from './components/SavedConnections.vue'
import FileManager from './components/FileManager.vue'
import Terminal from './components/Terminal.vue'
import LogPanel from './components/LogPanel.vue'
import EditorModal from './components/EditorModal.vue'

// 多连接：connections 保存所有活跃会话，activeId 标记当前查看的标签
const connections = ref([])
const activeId = ref(null)
const connectVisible = ref(false)
const logEnabled = ref(true)
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

async function loadSaved() {
  try {
    const res = await api.listConnections()
    savedList.value = res.connections || []
  } catch (_) { /* 忽略：下拉里会显示空 */ }
}
onMounted(loadSaved)

function toConn(payload) {
  return {
    connectionId: payload.connectionId,
    host: payload.host,
    username: payload.username,
    port: payload.port,
    authType: payload.authType,
    name: payload.name || '',
    homeDirectory: payload.homeDirectory || '/',
  }
}

function handleConnected(payload) {
  const conn = toConn(payload)
  // 同一 connectionId 去重（如重连同一台）
  const idx = connections.value.findIndex((c) => c.connectionId === conn.connectionId)
  if (idx >= 0) connections.value.splice(idx, 1, conn)
  else connections.value.push(conn)
  activeId.value = conn.connectionId
  connectVisible.value = false
  editor.value = { open: false, connId: null, path: null }
  loadSaved() // 刷新排序/别名
}

async function disconnectConn(connId) {
  try {
    await api.disconnect(connId)
  } catch (_) { /* 忽略：会话可能已被后端回收 */ }
  const idx = connections.value.findIndex((c) => c.connectionId === connId)
  if (idx >= 0) connections.value.splice(idx, 1)
  if (activeId.value === connId) {
    const rest = connections.value
    activeId.value = rest.length ? rest[rest.length - 1].connectionId : null
  }
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
  <div class="app">
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
        >
          <FileManager
            :conn-id="c.connectionId"
            :initial-dir="c.homeDirectory"
            @open-file="(p) => openEditor(c.connectionId, p)"
          />
          <Terminal :conn-id="c.connectionId" />
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
  display: grid;
  grid-template-columns: 1.4fr 1fr;
  gap: 18px;
  height: 100%;
  min-height: 0;
}
@media (max-width: 1000px) {
  .connect-area,
  .workspace {
    grid-template-columns: 1fr;
  }
}
</style>
