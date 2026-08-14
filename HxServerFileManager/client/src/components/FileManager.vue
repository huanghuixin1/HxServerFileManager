<script setup>
import { ref, onMounted, watch, computed } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api } from '../api.js'

const props = defineProps({
  connId: String,
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
const fileInput = ref(null)

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

onMounted(() => load())
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

// 终端 cd 后 App 推来新目录，文件列表跟随（syncCwd 关闭时 externalPath 为 null，自动忽略）
watch(
  () => props.externalPath,
  (p) => {
    if (p && p !== cwd.value) load(p)
  }
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
  try {
    await api.remove(props.connId, cwd.value, item.name)
    ElMessage.success(`已删除 ${item.name}`)
    await load()
  } catch (e) {
    error.value = e.message
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
  uploading.value = true
  error.value = ''
  try {
    for (const f of files) {
      await api.uploadFile(props.connId, cwd.value, f)
    }
    ElMessage.success(`成功上传 ${files.length} 个文件`)
    await load()
  } catch (e) {
    error.value = e.message
  } finally {
    uploading.value = false
    fileInput.value.value = ''
  }
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
        <el-button size="small" type="primary" plain @click="newDirVisible = true">
          <el-icon style="margin-right: 4px"><FolderAdd /></el-icon>新建目录
        </el-button>
        <el-button size="small" :loading="uploading" @click="triggerUpload">
          <el-icon style="margin-right: 4px"><Upload /></el-icon>{{ uploading ? '上传中…' : '上传' }}
        </el-button>
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
      :data="items"
      class="fm-table"
      v-loading="loading"
      row-key="fullPath"
      empty-text="空目录"
      @row-dblclick="(row) => (row.isDirectory ? openDir(row) : row.isText && emit('open-file', row.fullPath))"
    >
      <el-table-column label="名称" min-width="240">
        <template #default="{ row }">
          <span
            class="fname"
            :class="{ dir: row.isDirectory, text: row.isText }"
            @click="row.isDirectory ? openDir(row) : (row.isText && emit('open-file', row.fullPath))"
          >
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
      <el-table-column label="操作" width="230" align="right">
        <template #default="{ row }">
          <el-button v-if="row.isText" link type="primary" size="small" @click.stop="emit('open-file', row.fullPath)">编辑</el-button>
          <el-button link type="primary" size="small" @click.stop="download(row)">下载</el-button>
          <el-button link size="small" @click.stop="startRename(row)">重命名</el-button>
          <el-button link type="danger" size="small" @click.stop="remove(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

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
  </div>
</template>

<style scoped>
.fm {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
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
</style>
