<script setup>
import { ref, onMounted, onUnmounted, watch, computed } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, getToken, isDesktop, desktopDownloadFile, desktopDownloadMany, desktopUploadDropped, onDesktopEvent } from '../api.js'
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
  hasOtherConns: { type: Boolean, default: false }, // 是否存在其他活跃连接（决定「发送到连接」是否可用）
  refreshToken: { type: Number, default: 0 }, // App 在服务器间直传完成后 +1，触发本列表重新加载
  active: { type: Boolean, default: true }, // 是否为当前激活标签（Linux 桌面拖放消息只让激活标签响应）
})
const emit = defineEmits(['open-file', 'navigate', 'update:sync-cwd', 'send-to-connection'])

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
// 批量下载（多选文件/文件夹 → 桌面端选一个文件夹整包下载）状态
const downloading = ref(false)
const downloadName = ref('')
const downloadCount = ref(0)
let downloadCancel = null // 桌面端：desktopDownloadMany 返回的 cancel()（停止下载按钮）
let downloadAbort = false // 浏览器端：逐个 <a download> 循环的终止标记
let downloadCanceled = false // 用户主动点了「停止下载」
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
const tableLoading = computed(() => loading.value || deleting.value || uploading.value || downloading.value)
const loadingText = computed(() => {
  if (uploading.value) return ''
  if (deleting.value) return '删除中…'
  if (downloading.value) return '下载中…'
  return ''
})

// ---- 行右键菜单（替代操作列）----
const menuVisible = ref(false)
const menuRow = ref(null)
const menuX = ref(0)
const menuY = ref(0)

function onRowContextMenu(row, _col, event) {
  event.preventDefault()
  // 标准文件管理器行为：右键未选中的行时先把选中集切到该行；右键多选中的成员则保持多选
  if (!selectedSet.value.has(row.fullPath)) {
    tableRef.value?.clearSelection()
    tableRef.value?.toggleRowSelection(row, true)
    lastSelected = row
  }
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
  if (!row) return
  // 右键的是多选中的一员：下载整个选中集（批量，选文件夹）；否则单文件
  if (selectedItems.value.length > 1 && selectedSet.value.has(row.fullPath)) {
    downloadSelected()
    return
  }
  download(row)
}
function menuRename() {
  const row = menuRow.value
  closeMenu()
  if (row) startRename(row)
}
function menuDelete() {
  const row = menuRow.value
  closeMenu()
  if (!row) return
  // 右键的是多选中的一员：删除整个选中集（与「操作→删除」一致）；否则单文件
  if (selectedItems.value.length > 1 && selectedSet.value.has(row.fullPath)) {
    batchDelete()
    return
  }
  remove(row)
}

onMounted(() => document.addEventListener('click', closeMenu))
onUnmounted(() => document.removeEventListener('click', closeMenu))

// Linux 桌面壳拖放事件订阅的取消函数（onMounted 注册，onUnmounted 注销）
let unsubDragState = null
let unsubDrop = null
onUnmounted(() => {
  unsubDragState?.()
  unsubDrop?.()
})

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

// 「操作」下拉：新建目录 / 上传 / 发送到连接 / 批量删除
function onToolCommand(cmd) {
  if (cmd === 'mkdir') newDirVisible.value = true
  else if (cmd === 'upload') triggerUpload()
  else if (cmd === 'download') downloadSelected()
  else if (cmd === 'send') {
    emit('send-to-connection', selectedItems.value.map((i) => i.fullPath))
  } else if (cmd === 'delete') batchDelete()
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
  // Linux 桌面壳拖放（C# 消息桥）：只响应当前激活标签，隐藏标签的 FileManager 忽略
  unsubDragState = onDesktopEvent('desktopDragState', (msg) => {
    if (!props.active) return
    desktopDragActive = !!msg.active
    dragOver.value = desktopDragActive || domDragDepth > 0
  })
  unsubDrop = onDesktopEvent('desktopDrop', (msg) => {
    if (!props.active) return
    desktopDragActive = false
    dragOver.value = false
    if (msg?.paths?.length) runDesktopDropUpload(msg.paths)
  })
})
watch(() => props.connId, () => load(props.initialDir))

// 服务器间直传完成后，App 把目标连接对应的 refreshToken +1，这里收到后重新加载列表
watch(
  () => props.refreshToken,
  (v) => {
    if (v > 0) load()
  }
)

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

async function download(item) {
  const name = item.name || baseName(item.fullPath) || 'download'
  // 桌面壳：弹原生「另存为」对话框选保存位置，由 C# 端流式下载落盘（大文件不走 JS 桥）
  if (await isDesktop()) {
    const url = new URL(api.downloadUrl(props.connId, item.fullPath), location.href).href
    try {
      const path = await desktopDownloadFile(url, name)
      if (path) ElMessage.success(`已下载到 ${path}`)
    } catch (e) {
      ElMessage.error(e.message)
    }
    return
  }
  const a = document.createElement('a')
  a.href = api.downloadUrl(props.connId, item.fullPath)
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
}

// 批量下载（操作菜单）：选中多个文件/文件夹 → 桌面端只选一个本地文件夹，整包下载保留目录结构
async function downloadSelected() {
  const sel = selectedItems.value
  if (!sel.length) {
    ElMessage.warning('请先选中要下载的文件')
    return
  }
  // 单个文件保持原「另存为」行为（可改文件名）；多选或含目录走「选文件夹」批量下载
  if (sel.length === 1 && !sel[0].isDirectory) {
    download(sel[0])
    return
  }
  if (await isDesktop()) {
    const url = new URL(api.downloadManyUrl(props.connId), location.href).href
    const paths = sel.map((i) => i.fullPath)
    downloading.value = true
    downloadCount.value = 0
    downloadCanceled = false
    downloadCancel = null
    try {
      const { promise, cancel } = desktopDownloadMany(url, paths, (file) => {
        downloadName.value = file
        downloadCount.value++
      })
      downloadCancel = cancel
      const res = await promise
      if (res) ElMessage.success(`已下载 ${res.count} 个文件到 ${res.path}`)
      else if (downloadCanceled) ElMessage.info('已取消批量下载')
    } catch (e) {
      ElMessage.error(e.message)
    } finally {
      downloading.value = false
      downloadCancel = null
    }
    return
  }
  // 浏览器：无选文件夹能力，逐个下载文件（目录结构丢失，提示）
  const dirs = sel.filter((i) => i.isDirectory)
  if (dirs.length) ElMessage.warning('浏览器不支持选择保存文件夹，选中的目录将跳过，只下载文件')
  const files = sel.filter((i) => !i.isDirectory)
  downloading.value = true
  downloadCount.value = 0
  downloadCanceled = false
  downloadAbort = false
  try {
    for (let i = 0; i < files.length && !downloadAbort; i++) {
      const f = files[i]
      downloadName.value = f.name
      downloadCount.value = i + 1
      const a = document.createElement('a')
      a.href = api.downloadUrl(props.connId, f.fullPath)
      a.download = f.name
      document.body.appendChild(a)
      a.click()
      a.remove()
      if (!downloadAbort) await new Promise((r) => setTimeout(r, 300)) // 留间隔避免被浏览器拦成「连续下载」
    }
    if (downloadAbort) ElMessage.info('已取消批量下载')
  } finally {
    downloading.value = false
  }
}

// 「停止下载」：桌面端取消 C# 在途任务（远端 tar 随之终止、清理已解包部分文件）；
// 浏览器端终止逐个下载循环
function stopDownload() {
  downloadCanceled = true
  downloadAbort = true
  downloadCancel?.()
  downloadCancel = null
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

// 路径拼接（cwd 可能以 / 结尾或为 /）
function joinPath(dir, name) {
  if (!dir || dir === '/') return `/${name}`
  return `${dir.replace(/\/+$/, '')}/${name}`
}

// 预校验：单个文件超上限直接拦截，避免传一半才 413；后端不限制（Infinity）时跳过。
// 返回是否通过；不通过时已弹错误提示
function checkUploadLimits(items) {
  const limited = Number.isFinite(uploadLimitBytes.value)
  const tooBig = limited ? items.filter((it) => it.file.size > uploadLimitBytes.value) : []
  if (tooBig.length) {
    const limitMb = Math.round(uploadLimitBytes.value / 1024 / 1024)
    ElMessage.error(
      `以下文件超过单文件 ${limitMb}MB 上限，未开始上传：${tooBig.map((it) => it.name).join('、')}`
    )
    return false
  }
  return true
}

// 统一上传队列：先建目录（父级在前，含空目录；已存在则后端跳过），再逐文件上传。
// items = [{ file, relDir, name }]；relDir 为相对当前目录的子路径（'' = 直接放当前目录）
async function runUploads(dirs, items) {
  uploading.value = true
  error.value = ''
  uploadTotal.value = items.length
  uploadAbort.value = new AbortController()
  try {
    if (dirs.length && !uploadAbort.value.signal.aborted) {
      await api.ensureDirs(props.connId, cwd.value, dirs)
    }
    for (let i = 0; i < items.length; i++) {
      const it = items[i]
      uploadIndex.value = i + 1
      uploadName.value = it.relDir ? `${it.relDir}/${it.name}` : it.name
      uploadProgress.value = 0
      await api.uploadFile(
        props.connId,
        it.relDir ? joinPath(cwd.value, it.relDir) : cwd.value,
        it.file,
        (p) => {
          uploadProgress.value = p
        },
        uploadAbort.value.signal
      )
    }
    if (uploadAbort.value?.signal.aborted) {
      ElMessage.info('已取消上传')
    } else {
      ElMessage.success(`成功上传 ${items.length} 个文件`)
      await load()
    }
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

async function onFileSelected(e) {
  const files = e.target.files
  if (!files || files.length === 0) return
  const items = [...files].map((f) => ({ file: f, relDir: '', name: f.name }))
  if (!checkUploadLimits(items)) return
  await runUploads([], items)
}

// 递归遍历选中的目录（FileSystemEntry）：
// dirs 收集全部目录相对路径（含空目录，父级在前），items 收集文件
async function collectEntries(entry, basePath, dirs, items) {
  if (entry.isFile) {
    const file = await new Promise((resolve, reject) => entry.file(resolve, reject))
    items.push({ file, relDir: basePath, name: entry.name })
    return
  }
  const dirPath = basePath ? `${basePath}/${entry.name}` : entry.name
  dirs.push(dirPath)
  const reader = entry.createReader()
  // readEntries 每次最多返回 100 条，需循环读到空为止
  for (;;) {
    const batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject))
    if (!batch.length) break
    for (const child of batch) await collectEntries(child, dirPath, dirs, items)
  }
}

// ---- 拖拽上传提示层 ----
// Windows/浏览器：DOM drag 事件驱动；Linux（WebKitGTK 外部文件拖放有 bug，DOM 事件不触发）：
// 由桌面壳 GTK 层接管拖放，经消息桥发 desktopDragState/desktopDrop 驱动（只响应当前激活标签）。
const dragOver = ref(false)
let domDragDepth = 0 // DOM 拖入/拖出深度计数（子元素 enter/leave 成对出现）
let desktopDragActive = false // 桌面壳（Linux GTK）拖拽中

function onDragEnter() {
  domDragDepth++
  dragOver.value = desktopDragActive || domDragDepth > 0
}
function onDragLeave() {
  domDragDepth = Math.max(0, domDragDepth - 1)
  if (domDragDepth === 0) dragOver.value = desktopDragActive
}

async function onDrop(e) {
  domDragDepth = 0
  dragOver.value = false
  if (uploading.value) {
    ElMessage.warning('已有上传任务进行中，请先完成或停止')
    return
  }
  const dirs = []
  const items = []
  // webkitGetAsEntry 在部分 WebKit（含 WebKitGTK）缺失，直接调用会抛异常吞掉整个 onDrop，
  // 这里容错：取不到时退回下面的 dataTransfer.files 兜底（文件夹会丢失，仅浏览器端）
  let entries = []
  try {
    entries = [...(e.dataTransfer?.items || [])]
      .filter((it) => it.kind === 'file')
      .map((it) => it.webkitGetAsEntry?.() || null)
      .filter(Boolean)
  } catch (_) {
    entries = []
  }
  if (entries.length) {
    for (const entry of entries) {
      if (entry.isFile) {
        const file = await new Promise((resolve, reject) => entry.file(resolve, reject))
        items.push({ file, relDir: '', name: entry.name })
      } else {
        // 文件夹：递归遍历（含空目录、保留相对结构）
        await collectEntries(entry, '', dirs, items)
      }
    }
  } else {
    // 兼容兜底：webkitGetAsEntry 不可用时退回 dataTransfer.files（文件夹会丢失）
    for (const f of e.dataTransfer?.files || []) {
      items.push({ file: f, relDir: '', name: f.name })
    }
  }
  if (!items.length && !dirs.length) return
  if (!checkUploadLimits(items)) return
  await runUploads(dirs, items)
}

// ---- Linux 桌面壳拖拽上传 ----
// C# 的 GTK 层拿到本地路径列表后经 desktopDrop 消息回传（JS 无法读任意本地路径），
// 这里把连接信息回传，由壳进程代读代传（复用 /api/ensure-dirs + /api/upload），进度面板与手动上传共用
async function runDesktopDropUpload(paths) {
  if (uploading.value) {
    ElMessage.warning('已有上传任务进行中，请先完成或停止')
    return
  }
  uploading.value = true
  error.value = ''
  uploadIndex.value = 0
  uploadTotal.value = paths.length
  uploadName.value = '正在扫描文件…'
  uploadProgress.value = 0
  try {
    const { promise, cancel } = desktopUploadDropped(
      location.origin,
      props.connId,
      cwd.value,
      paths,
      getToken(),
      (msg) => {
        uploadIndex.value = msg.index
        uploadTotal.value = msg.total
        uploadName.value = msg.name
        uploadProgress.value = msg.percent ?? 0
      }
    )
    droppedUploadCancel = cancel
    const res = await promise
    if (res) {
      ElMessage.success(`成功上传 ${res.count} 个文件`)
      await load()
    } else {
      ElMessage.info('已取消上传')
    }
  } catch (e) {
    error.value = e.message
  } finally {
    uploading.value = false
    droppedUploadCancel = null
  }
}

let droppedUploadCancel = null // Linux 桌面拖拽上传的取消函数（「停止上传」按钮）
function stopUpload() {
  uploadAbort.value?.abort()
  droppedUploadCancel?.()
  droppedUploadCancel = null
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
  <div
    class="card fm"
    @dragenter.prevent="onDragEnter"
    @dragover.prevent
    @dragleave.prevent="onDragLeave"
    @drop.prevent="onDrop"
  >
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
              <el-dropdown-item command="download" :disabled="downloading || selectedItems.length === 0">
                <el-icon style="margin-right: 6px"><Download /></el-icon>{{ downloading ? '下载中…' : '下载' }}
                <span v-if="selectedItems.length" class="dd-count">{{ selectedItems.length }}</span>
              </el-dropdown-item>
              <el-dropdown-item
                command="send"
                :disabled="selectedItems.length === 0 || !hasOtherConns"
                :title="!hasOtherConns ? '需要同时打开至少两个连接' : ''"
              >
                <el-icon style="margin-right: 6px"><Share /></el-icon>发送到连接…
                <span v-if="selectedItems.length" class="dd-count">{{ selectedItems.length }}</span>
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

    <!-- 批量下载进度面板：当前解包的文件名 + 不定进度条（tar 流条目数不可预知，用不定长进度）+ 停止下载 -->
    <div v-if="downloading" class="upload-panel">
      <div class="up-line">
        <span class="up-name" :title="downloadName">{{ downloadName || '正在连接…' }}</span>
        <span class="up-count">已 {{ downloadCount }}</span>
      </div>
      <el-progress :percentage="100" :indeterminate="true" :duration="2" :stroke-width="8" :show-text="false" />
      <div class="up-foot">
        <span class="up-pct">下载中…</span>
        <el-button size="small" type="danger" plain @click="stopDownload">停止下载</el-button>
      </div>
    </div>

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

    <!-- 拖拽上传提示层（拖入文件/文件夹时显示） -->
    <div v-if="dragOver" class="drop-overlay">
      <div class="drop-hint">松开以上传文件 / 文件夹</div>
    </div>
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

/* 拖拽上传提示层：盖住整个面板（z-index 需高于 upload-panel 的 3000） */
.drop-overlay {
  position: absolute;
  inset: 0;
  z-index: 4000;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(45, 108, 223, 0.06);
  border: 2px dashed #2d6cdf;
  border-radius: 10px;
  pointer-events: none;
}
.drop-hint {
  padding: 14px 28px;
  background: #fff;
  border-radius: 10px;
  box-shadow: 0 6px 24px rgba(0, 0, 0, 0.16);
  color: #2d6cdf;
  font-size: 14px;
  font-weight: 600;
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
.dd-count {
  margin-left: auto;
  font-size: 12px;
  color: #8a97a5;
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
