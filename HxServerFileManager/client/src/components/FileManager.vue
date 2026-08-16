<script setup>
import { ref, onMounted, onUnmounted, watch, computed } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api } from '../api.js'
import { useSettings } from '../useSettings.js'

// 常用目录收藏（后端 Data/settings.json）：按连接隔离，跳转/添加/管理
const { favorites, ensureLoaded, newId, saveFavorites } = useSettings()
const favManagerVisible = ref(false) // 「管理收藏」列表对话框
const favEditVisible = ref(false) // 新增/编辑收藏表单
const favEditing = ref(null) // null = 新增，否则为正在编辑的收藏对象
const favEditName = ref('')
const favEditPath = ref('')

// 收藏归属判断：优先 connKey（稳定连接标识，重连后 connectionId 会变仍归属同一台服务器）；
// 兼容旧数据（此前按 connectionId 存的），旧数据在新会话下不再匹配属预期
function favBelongs(f) {
  return (f.connKey && f.connKey === props.connKey) || (f.connectionId && f.connectionId === props.connId)
}

const connFavs = computed(() => favorites.value.filter(favBelongs))

function baseName(p) {
  const s = String(p || '/').replace(/\/+$/, '')
  return s === '' ? '/' : s.slice(s.lastIndexOf('/') + 1)
}

const cwdIsFav = computed(() => connFavs.value.some((f) => f.path === cwd.value))

async function toggleFav() {
  const wasFav = cwdIsFav.value
  try {
    if (wasFav) {
      favorites.value = favorites.value.filter((f) => !(favBelongs(f) && f.path === cwd.value))
    } else {
      const now = new Date().toISOString()
      favorites.value.push({
        id: newId(),
        connKey: props.connKey,
        name: baseName(cwd.value),
        path: cwd.value,
        createdAt: now,
        updatedAt: now,
      })
    }
    await saveFavorites()
    ElMessage.success(wasFav ? '已取消收藏' : '已收藏')
  } catch (e) {
    ElMessage.error(e.message)
  }
}

function onFavCommand(cmd) {
  if (cmd === 'toggle') toggleFav()
  else if (cmd === 'manage') openFavManager()
  else if (cmd && cmd.type === 'jump') goPath(cmd.fav.path)
}

function openFavManager() {
  favManagerVisible.value = true
}

function startAddFav() {
  favEditing.value = null
  favEditName.value = baseName(cwd.value)
  favEditPath.value = cwd.value
  favEditVisible.value = true
}

function startEditFav(f) {
  favEditing.value = f
  favEditName.value = f.name
  favEditPath.value = f.path
  favEditVisible.value = true
}

async function saveFavForm() {
  const name = favEditName.value.trim()
  const path = favEditPath.value.trim()
  if (!name || !path) {
    ElMessage.warning('名称和路径不能为空')
    return
  }
  try {
    const now = new Date().toISOString()
    if (favEditing.value) {
      favEditing.value.name = name
      favEditing.value.path = path.replace(/\/+$/, '') || '/'
      favEditing.value.updatedAt = now
    } else {
      favorites.value.push({
        id: newId(),
        connKey: props.connKey,
        name,
        path: path.replace(/\/+$/, '') || '/',
        createdAt: now,
        updatedAt: now,
      })
    }
    await saveFavorites()
    ElMessage.success('已保存')
    favEditVisible.value = false
    favEditing.value = null
  } catch (e) {
    ElMessage.error(e.message)
  }
}

async function removeFav(f) {
  try {
    await ElMessageBox.confirm(`确定删除收藏 “${f.name}”？`, '删除收藏', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消',
    })
  } catch (_) {
    return
  }
  favorites.value = favorites.value.filter((x) => x !== f)
  try {
    await saveFavorites()
    ElMessage.success('已删除')
  } catch (e) {
    ElMessage.error(e.message)
  }
}

const props = defineProps({
  connId: String,
  connKey: { type: String, default: '' }, // 连接稳定标识（profileId 或 host@user:port），收藏按此隔离
  initialDir: { type: String, default: '/' },
  syncCwd: { type: Boolean, default: true },
  externalPath: { type: String, default: null }, // 终端推送的目录（syncCwd 开启时跟随）
})
const emit = defineEmits(['open-file', 'navigate', 'update:sync-cwd'])

const cwd = ref(props.initialDir || '/')
const items = ref([])
const loading = ref(false)
const error = ref('')

const newDirVisible = ref(false)
const newDirName = ref('')
const creating = ref(false)

const renameVisible = ref(false)
const renameValue = ref('')
const renaming = ref(null)

const uploading = ref(false)
const deleting = ref(false)
const fileInput = ref(null)
// 上传进度：当前文件 i/n、文件名、百分比；uploadAbort 用于「停止上传」
const uploadIndex = ref(0)
const uploadTotal = ref(0)
const uploadName = ref('')
const uploadProgress = ref(0)
const uploadAbort = ref(null)
// 单文件上传上限（字节）：默认 1GB，挂载时从 /api/health 读取后端实际配置
// （HXSFM_MAX_UPLOAD_MB / env.json maxUploadMb；health 返回 0 表示不限制 → Infinity）
const uploadLimitBytes = ref(1024 * 1024 * 1024)

// 列表加载中状态：目录刷新 / 删除 / 上传共用遮罩，附文字区分（上传详情见 upload-panel）
const tableLoading = computed(() => loading.value || deleting.value || uploading.value)
const loadingText = computed(() => {
  if (uploading.value) return ''
  if (deleting.value) return '删除中…'
  return ''
})

// ---- 行右键菜单（替代操作列）----
const menuVisible = ref(false)
const menuRow = ref(null)
const menuX = ref(0)
const menuY = ref(0)

function onRowContextMenu(row, _col, event) {
  event.preventDefault()
  menuRow.value = row
  menuX.value = Math.min(event.clientX, window.innerWidth - 150)
  menuY.value = Math.min(event.clientY, window.innerHeight - 200)
  menuVisible.value = true
}
function closeMenu() {
  menuVisible.value = false
  menuRow.value = null
}
function menuEdit() {
  const row = menuRow.value
  closeMenu()
  // 不依赖 isText 识别：非目录都能尝试编辑（后端会拒绝二进制/超大文件）
  if (row && !row.isDirectory) emit('open-file', row.fullPath)
}
function menuDownload() {
  const row = menuRow.value
  closeMenu()
  if (row) download(row)
}
function menuRename() {
  const row = menuRow.value
  closeMenu()
  if (row) startRename(row)
}
function menuDelete() {
  const row = menuRow.value
  closeMenu()
  if (row) remove(row)
}

onMounted(() => document.addEventListener('click', closeMenu))
onUnmounted(() => document.removeEventListener('click', closeMenu))

// ---- 行单选/多选（Shift 范围选，Ctrl/Cmd 加减选，类似 Windows/Mac 文件管理器）----
const tableRef = ref(null)
const selectedSet = ref(new Set()) // 选中项的 fullPath
let lastSelected = null

function onSelectionChange(rows) {
  selectedSet.value = new Set(rows.map((r) => r.fullPath))
}
function rowClassName({ row }) {
  return selectedSet.value.has(row.fullPath) ? 'row-selected' : ''
}
function onRowClick(row, _col, event) {
  const table = tableRef.value
  if (!table) return
  if (event.shiftKey && lastSelected) {
    // Shift：上次选中到当前行的范围全部选中
    const start = items.value.indexOf(lastSelected)
    const end = items.value.indexOf(row)
    if (start >= 0 && end >= 0) {
      const range = items.value.slice(Math.min(start, end), Math.max(start, end) + 1)
      for (const r of range) table.toggleRowSelection(r, true)
    }
    lastSelected = row
  } else if (event.ctrlKey || event.metaKey) {
    // Ctrl/Cmd：切换当前行选中（不重置其他）
    table.toggleRowSelection(row, !selectedSet.value.has(row.fullPath))
    lastSelected = row
  } else {
    // 普通单击：单选
    table.clearSelection()
    table.toggleRowSelection(row, true)
    lastSelected = row
  }
}

// 「操作」下拉：新建目录 / 上传 / 批量删除
function onToolCommand(cmd) {
  if (cmd === 'mkdir') newDirVisible.value = true
  else if (cmd === 'upload') triggerUpload()
  else if (cmd === 'delete') batchDelete()
}

// 当前选中项（来自当前目录列表）
const selectedItems = computed(() => items.value.filter((i) => selectedSet.value.has(i.fullPath)))

async function batchDelete() {
  const sel = selectedItems.value
  if (!sel.length) {
    ElMessage.warning('请先选中要删除的文件')
    return
  }
  try {
    await ElMessageBox.confirm(
      `确定删除选中的 ${sel.length} 项（${sel.some((i) => i.isDirectory) ? '含目录' : '文件'}）？此操作不可撤销。`,
      '批量删除',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' }
    )
  } catch (_) {
    return // 用户取消
  }
  error.value = ''
  deleting.value = true
  try {
    for (const item of sel) {
      await api.remove(props.connId, parentDir(item.fullPath), item.name)
    }
    ElMessage.success(`已删除 ${sel.length} 项`)
    tableRef.value?.clearSelection()
    lastSelected = null
    selectedSet.value = new Set()
    await load()
  } catch (e) {
    error.value = e.message
  } finally {
    deleting.value = false
  }
}

function combinePath(dir, name) {
  dir = (dir || '/').replace(/\/+$/, '')
  return (dir || '/') + '/' + String(name).replace(/^\/+/, '')
}

function parentDir(p) {
  p = (p || '/').replace(/\/+$/, '')
  if (p === '' || p === '/') return '/'
  const i = p.lastIndexOf('/')
  return i <= 0 ? '/' : p.slice(0, i)
}

const breadcrumbs = computed(() => {
  const parts = (cwd.value || '/').split('/').filter(Boolean)
  const acc = []
  let p = ''
  for (const part of parts) {
    p += '/' + part
    acc.push({ name: part, path: p })
  }
  return acc
})

async function load(dir) {
  if (dir !== undefined) cwd.value = dir
  loading.value = true
  error.value = ''
  try {
    const res = await api.listFiles(props.connId, cwd.value)
    items.value = res.items || []
    if (res.path) cwd.value = res.path
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  load()
  ensureLoaded()
  // 读取后端实际上传上限（失败则保持默认 1GB，服务端仍会兜底 413）；
  // maxUploadBytes <= 0 表示后端不限制（桌面壳忽略大小上限），前端同样不拦截
  api
    .health()
    .then((h) => {
      if (!h || h.maxUploadBytes == null) return
      uploadLimitBytes.value = h.maxUploadBytes > 0 ? h.maxUploadBytes : Infinity
    })
    .catch(() => {})
})
watch(() => props.connId, () => load(props.initialDir))

// 用户主动导航：跳目录并通知 App 同步会话 cwd（终端提示符/下一条命令跟随）
function goPath(p) {
  load(p)
  emit('navigate', p)
}
function openDir(item) {
  if (item.isDirectory) goPath(item.fullPath)
}
function goUp() {
  goPath(parentDir(cwd.value))
}

// 终端 cd 后 App 推来新目录，文件列表跟随（syncCwd 关闭时 externalPath 为 null，自动忽略）。
// immediate：刷新恢复路径时挂载即可能已带 externalPath，否则首次不触发
watch(
  () => props.externalPath,
  (p) => {
    if (p && p !== cwd.value) load(p)
  },
  { immediate: true }
)

function download(item) {
  const a = document.createElement('a')
  a.href = api.downloadUrl(props.connId, item.fullPath)
  a.download = item.name
  document.body.appendChild(a)
  a.click()
  a.remove()
}

async function remove(item) {
  try {
    await ElMessageBox.confirm(
      `确定删除${item.isDirectory ? '目录' : '文件'} “${item.name}” ？此操作不可撤销。`,
      '删除确认',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' }
    )
  } catch (_) {
    return // 用户取消
  }
  error.value = ''
  deleting.value = true
  try {
    await api.remove(props.connId, cwd.value, item.name)
    ElMessage.success(`已删除 ${item.name}`)
    await load()
  } catch (e) {
    error.value = e.message
  } finally {
    deleting.value = false
  }
}

function startRename(item) {
  renaming.value = item
  renameValue.value = item.name
  renameVisible.value = true
}
async function doRename() {
  if (!renaming.value) return
  const newName = renameValue.value.trim()
  if (!newName) {
    ElMessage.warning('名称不能为空')
    return
  }
  if (newName === renaming.value.name) {
    renameVisible.value = false
    renaming.value = null
    return
  }
  error.value = ''
  try {
    await api.rename(props.connId, cwd.value, renaming.value.name, combinePath(cwd.value, newName))
    ElMessage.success('重命名成功')
    renameVisible.value = false
    renaming.value = null
    await load()
  } catch (e) {
    error.value = e.message
  }
}

function triggerUpload() {
  fileInput.value.click()
}
async function onFileSelected(e) {
  const files = e.target.files
  if (!files || files.length === 0) return

  // 预校验：单个文件超上限直接拦截，避免传一半才 413；后端不限制（Infinity）时跳过
  const limited = Number.isFinite(uploadLimitBytes.value)
  const tooBig = limited ? [...files].filter((f) => f.size > uploadLimitBytes.value) : []
  if (tooBig.length) {
    const limitMb = Math.round(uploadLimitBytes.value / 1024 / 1024)
    ElMessage.error(
      `以下文件超过单文件 ${limitMb}MB 上限，未开始上传：${tooBig.map((f) => f.name).join('、')}`
    )
    fileInput.value.value = ''
    return
  }

  uploading.value = true
  error.value = ''
  uploadTotal.value = files.length
  uploadAbort.value = new AbortController()
  try {
    for (let i = 0; i < files.length; i++) {
      const f = files[i]
      uploadIndex.value = i + 1
      uploadName.value = f.name
      uploadProgress.value = 0
      await api.uploadFile(props.connId, cwd.value, f, (p) => {
        uploadProgress.value = p
      }, uploadAbort.value.signal)
    }
    ElMessage.success(`成功上传 ${files.length} 个文件`)
    await load()
  } catch (e) {
    if (uploadAbort.value?.signal.aborted) {
      ElMessage.info('已取消上传')
    } else {
      error.value = e.message
    }
  } finally {
    uploading.value = false
    uploadAbort.value = null
    fileInput.value.value = ''
  }
}

function stopUpload() {
  uploadAbort.value?.abort()
}

async function createDir() {
  const name = newDirName.value.trim()
  if (!name) {
    ElMessage.warning('请输入目录名')
    return
  }
  creating.value = true
  error.value = ''
  try {
    await api.mkdir(props.connId, cwd.value, name)
    ElMessage.success(`已创建 ${name}`)
    newDirVisible.value = false
    newDirName.value = ''
    await load()
  } catch (e) {
    error.value = e.message
  } finally {
    creating.value = false
  }
}

function fmtSize(n) {
  if (n == null) return '-'
  if (n < 1024) return n + ' B'
  if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB'
  if (n < 1024 * 1024 * 1024) return (n / 1024 / 1024).toFixed(1) + ' MB'
  return (n / 1024 / 1024 / 1024).toFixed(1) + ' GB'
}
function fmtDate(s) {
  if (!s) return ''
  const d = new Date(s)
  if (isNaN(d)) return s
  const p = (n) => String(n).padStart(2, '0')
  // 精确到分钟，去掉秒
  return `${d.getFullYear()}/${p(d.getMonth() + 1)}/${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
}
</script>

<template>
  <div class="card fm">
    <div class="fm-toolbar">
      <div class="crumbs">
        <span class="crumb-link root" @click="goPath('/')">/</span>
        <template v-for="(b, i) in breadcrumbs" :key="b.path">
          <span v-if="i > 0" class="sep">/</span>
          <span
            class="crumb-link"
            :class="{ last: i === breadcrumbs.length - 1 }"
            @click="goPath(b.path)"
            >{{ b.name }}</span
          >
        </template>
      </div>

      <div class="tools">
        <el-checkbox
          :model-value="syncCwd"
          @update:model-value="emit('update:sync-cwd', $event)"            class="sync-cb"
            >同步路径</el-checkbox
          >
        <el-button size="small" @click="goUp">
          <el-icon style="margin-right: 4px"><Top /></el-icon>上级
        </el-button>
        <el-button size="small" @click="load()">
          <el-icon style="margin-right: 4px"><Refresh /></el-icon>刷新
        </el-button>
        <el-dropdown trigger="click" @command="onFavCommand">
          <el-button size="small" type="warning" plain>
            <el-icon style="margin-right: 4px"><Star /></el-icon>收藏
            <el-icon class="el-icon--right"><ArrowDown /></el-icon>
          </el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item
                command="toggle"
                :icon="cwdIsFav ? 'StarFilled' : 'Star'"
              >
                {{ cwdIsFav ? '取消收藏当前目录' : `收藏当前目录（${baseName(cwd)}）` }}
              </el-dropdown-item>
              <el-dropdown-item command="manage">
                <el-icon style="margin-right: 6px"><Setting /></el-icon>管理收藏…
              </el-dropdown-item>
              <el-dropdown-item v-if="connFavs.length" divided disabled>
                —— {{ connFavs.length }} 个收藏 ——
              </el-dropdown-item>
              <el-dropdown-item
                v-for="f in connFavs"
                :key="f.id"
                :command="{ type: 'jump', fav: f }"
              >
                <el-icon style="margin-right: 6px"><FolderOpened /></el-icon>
                <span class="fav-name" :title="f.path">{{ f.name }}</span>
                <span class="fav-path">{{ f.path }}</span>
              </el-dropdown-item>
              <el-dropdown-item v-if="!connFavs.length" divided disabled>
                收藏后会出现在这里，点击直达
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <el-dropdown trigger="click" @command="onToolCommand">
          <el-button size="small" type="primary" plain>
            <el-icon style="margin-right: 4px"><Menu /></el-icon>操作
            <el-icon class="el-icon--right"><ArrowDown /></el-icon>
          </el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="mkdir">
                <el-icon style="margin-right: 6px"><FolderAdd /></el-icon>新建目录
              </el-dropdown-item>
              <el-dropdown-item command="upload" :disabled="uploading">
                <el-icon style="margin-right: 6px"><Upload /></el-icon>{{ uploading ? '上传中…' : '上传' }}
              </el-dropdown-item>
              <el-dropdown-item command="delete" :disabled="selectedItems.length === 0" divided>
                <el-icon style="margin-right: 6px"><Delete /></el-icon>删除{{ selectedItems.length ? `（${selectedItems.length}）` : '' }}
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <input ref="fileInput" type="file" multiple hidden @change="onFileSelected" />
      </div>
    </div>

    <el-alert
      v-if="error"
      :title="error"
      type="error"
      :closable="false"
      show-icon
      class="mb"
    />

    <el-table
      ref="tableRef"
      :data="items"
      class="fm-table"
      v-loading="tableLoading"
      :element-loading-text="loadingText"
      row-key="fullPath"
      empty-text="空目录"
      :row-class-name="rowClassName"
      @row-click="onRowClick"
      @row-dblclick="(row) => (row.isDirectory ? openDir(row) : emit('open-file', row.fullPath))"
      @row-contextmenu="onRowContextMenu"
      @selection-change="onSelectionChange"
    >
      <template #empty>
        <div v-if="uploading" class="fm-empty">正在上传…</div>
        <span v-else>空目录</span>
      </template>
      <el-table-column type="selection" width="1" class-name="sel-col" />
      <el-table-column label="名称" min-width="240">
        <template #default="{ row }">
          <span class="fname" :class="{ dir: row.isDirectory, text: row.isText }">
            <el-icon class="fico"><Folder v-if="row.isDirectory" /><Document v-else-if="row.isText" /><Files v-else /></el-icon>
            {{ row.name }}
          </span>
        </template>
      </el-table-column>
      <el-table-column label="大小" width="110">
        <template #default="{ row }">{{ row.isDirectory ? '—' : fmtSize(row.size) }}</template>
      </el-table-column>
      <el-table-column label="修改时间" width="180">
        <template #default="{ row }">{{ fmtDate(row.lastWriteTimeUtc) }}</template>
      </el-table-column>
    </el-table>

    <!-- 上传进度面板：当前文件 i/n + 文件名 + 进度条 + 百分比 + 停止上传 -->
    <div v-if="uploading" class="upload-panel">
      <div class="up-line">
        <span class="up-name" :title="uploadName">{{ uploadName }}</span>
        <span class="up-count">{{ uploadIndex }}/{{ uploadTotal }}</span>
      </div>
      <el-progress :percentage="uploadProgress" :stroke-width="8" :show-text="false" />
      <div class="up-foot">
        <span class="up-pct">{{ uploadProgress }}%</span>
        <el-button size="small" type="danger" plain @click="stopUpload">停止上传</el-button>
      </div>
    </div>

    <!-- 行右键菜单：编辑/下载/重命名/删除 -->
    <div
      v-show="menuVisible"
      class="ctx-menu"
      :style="{ left: menuX + 'px', top: menuY + 'px' }"
      @click.stop
    >
      <div v-if="menuRow && !menuRow.isDirectory" class="ctx-item" @click="menuEdit">
        <el-icon :size="14"><EditPen /></el-icon>编辑
      </div>
      <div class="ctx-item" @click="menuDownload">
        <el-icon :size="14"><Download /></el-icon>下载
      </div>
      <div class="ctx-item" @click="menuRename">
        <el-icon :size="14"><Edit /></el-icon>重命名
      </div>
      <div class="ctx-item danger" @click="menuDelete">
        <el-icon :size="14"><Delete /></el-icon>删除
      </div>
    </div>

    <!-- 新建目录 -->
    <el-dialog
      v-model="newDirVisible"
      title="新建目录"
      width="420px"
      :close-on-click-modal="false"
    >
      <el-input
        v-model="newDirName"
        placeholder="目录名，例如 logs"
        @keyup.enter="createDir"
      />
      <template #footer>
        <el-button @click="newDirVisible = false">取消</el-button>
        <el-button type="primary" :loading="creating" @click="createDir">创建</el-button>
      </template>
    </el-dialog>

    <!-- 重命名 / 移动 -->
    <el-dialog
      v-model="renameVisible"
      title="重命名 / 移动"
      width="420px"
      :close-on-click-modal="false"
      @closed="renaming = null"
    >
      <el-input
        v-model="renameValue"
        placeholder="新名称（可带子路径实现移动）"
        @keyup.enter="doRename"
      />
      <template #footer>
        <el-button @click="renameVisible = false">取消</el-button>
        <el-button type="primary" @click="doRename">确定</el-button>
      </template>
    </el-dialog>

    <!-- 常用目录：收藏列表管理 -->
    <el-dialog
      v-model="favManagerVisible"
      title="常用目录"
      width="560px"
      :close-on-click-modal="false"
    >
      <el-table :data="connFavs" empty-text="还没有收藏，点击「新增收藏」保存当前目录" max-height="320">
        <el-table-column label="名称" min-width="140">
          <template #default="{ row }">{{ row.name }}</template>
        </el-table-column>
        <el-table-column label="路径" min-width="180">
          <template #default="{ row }">
            <span class="fav-path-cell" :title="row.path">{{ row.path }}</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="130" align="right">
          <template #default="{ row }">
            <el-button size="small" text type="primary" @click="startEditFav(row)">编辑</el-button>
            <el-button size="small" text type="danger" @click="removeFav(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
      <template #footer>
        <el-button @click="favManagerVisible = false">关闭</el-button>
        <el-button type="primary" plain @click="startAddFav">
          <el-icon style="margin-right: 4px"><Star /></el-icon>新增收藏
        </el-button>
      </template>
    </el-dialog>

    <!-- 常用目录：新增 / 编辑 -->
    <el-dialog
      v-model="favEditVisible"
      :title="favEditing ? '编辑收藏' : '新增收藏'"
      width="440px"
      :close-on-click-modal="false"
      @closed="favEditing = null"
    >
      <el-form label-width="56px" @submit.prevent="saveFavForm">
        <el-form-item label="名称">
          <el-input v-model="favEditName" placeholder="例如 项目日志" />
        </el-form-item>
        <el-form-item label="路径">
          <el-input v-model="favEditPath" placeholder="/var/log/app 或 /root" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="favEditVisible = false">取消</el-button>
        <el-button type="primary" @click="saveFavForm">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.fm {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  position: relative; /* 上传进度面板定位基准 */
  /* 屏蔽浏览器原生文本选中（行选择/拖拽时不出高亮） */
  user-select: none;
  -webkit-user-select: none;
}
.fm-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  margin-bottom: 10px;
}
.crumbs {
  flex: 1;
  min-width: 0;
  font-size: 13px;
  display: flex;
  align-items: center;
  white-space: nowrap;
  overflow: hidden;
}
.crumb-link {
  color: #2d6cdf;
  cursor: pointer;
  padding: 2px 3px;
  border-radius: 4px;
}
.crumb-link:hover {
  background: var(--accent-soft);
}
.crumb-link.last {
  color: #1f2d3d;
  font-weight: 600;
  cursor: default;
}
.crumb-link.last:hover {
  background: transparent;
}
.sep {
  color: #b6c0cc;
  flex-shrink: 0;
}
.sync-cb {
  margin-right: 4px;
  white-space: nowrap;
}
.fav-name {
  max-width: 120px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex-shrink: 0;
}
.fav-path {
  color: #9aa7b5;
  font-size: 12px;
  margin-left: 8px;
  max-width: 160px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.fav-path-cell {
  color: #6b7785;
  font-family: ui-monospace, monospace;
  font-size: 12.5px;
}
.tools {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
.mb {
  margin-bottom: 10px;
}
.fm-table {
  flex: 1;
  min-height: 0;
}

/* 上传进度面板：浮在列表上方（z-index 需高于 el-loading-mask 的 2000，否则被遮罩盖住） */
.upload-panel {
  position: absolute;
  top: 110px;
  left: 50%;
  transform: translateX(-50%);
  width: min(340px, 85%);
  z-index: 3000;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px;
  background: #fff;
  border: 1px solid var(--border);
  border-radius: 10px;
  box-shadow: 0 6px 24px rgba(0, 0, 0, 0.16);
}
.up-line {
  display: flex;
  align-items: center;
  gap: 10px;
}
.up-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 13px;
  color: #2a3542;
}
.up-count {
  flex-shrink: 0;
  font-size: 12px;
  color: #8a97a5;
}
.up-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.up-pct {
  font-size: 12px;
  color: #2d6cdf;
}
.fname {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 100%;
  cursor: default;
}
.fname .fico {
  flex-shrink: 0;
  color: #b3bfcc;
}
.fname.dir .fico {
  color: #e6a23c;
}
.fname.dir {
  cursor: pointer;
  color: #2d3a4b;
}
.fname.text {
  color: #2d6cdf;
  cursor: pointer;
}
.fname.text .fico {
  color: #7a8794;
}

/* 单选/多选：隐藏 selection 列 checkbox，行点击驱动选择 */
.fm-table :deep(.el-table__header .el-checkbox),
.fm-table :deep(.el-table__cell .el-checkbox) {
  display: none;
}
.fm-table :deep(.el-table__row.row-selected > td.el-table__cell) {
  background: #e3efff !important;
}
.fm-table :deep(.el-table__row.row-selected .fname) {
  color: #2d6cdf;
}

/* 行右键菜单 */
.ctx-menu {
  position: fixed;
  z-index: 3000;
  min-width: 130px;
  background: #fff;
  border: 1px solid var(--border);
  border-radius: 10px;
  box-shadow: 0 6px 20px rgba(0, 0, 0, 0.12);
  padding: 5px;
}
.ctx-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 12px;
  font-size: 13px;
  color: #2a3542;
  border-radius: 6px;
  cursor: pointer;
  user-select: none;
}
.ctx-item:hover {
  background: var(--accent-soft);
  color: #2d6cdf;
}
.ctx-item .el-icon {
  color: #8a97a5;
}
.ctx-item:hover .el-icon {
  color: #2d6cdf;
}
.ctx-item.danger {
  color: #d33;
}
.ctx-item.danger:hover {
  background: #fdecec;
  color: #c0392b;
}
.ctx-item.danger .el-icon {
  color: #e58a8a;
}
.ctx-item.danger:hover .el-icon {
  color: #c0392b;
}
</style>
