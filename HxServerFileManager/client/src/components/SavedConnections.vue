<script setup>
import { ref, onMounted, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, isDesktop, desktopSaveTextFile } from '../api.js'
import { useSettings } from '../useSettings.js'

// 代理标签（follow/custom 时展示，title 悬停可见具体配置）
const { proxyTagInfo, ensureLoaded } = useSettings()
ensureLoaded()

const props = defineProps({
  reloadToken: { type: Number, default: 0 },
})
const emit = defineEmits(['open', 'edit'])

const items = ref([])
const error = ref('')
const fileRef = ref(null)
const importing = ref(false)

async function load() {
  try {
    const res = await api.listConnections()
    items.value = res.connections || []
  } catch (e) {
    error.value = e.message
  }
}
onMounted(load)
watch(() => props.reloadToken, load)

// 导出：服务端解密后返回含凭据的明文 JSON，写为文件（备份/迁移用）。
// 桌面壳：弹原生「另存为」对话框让用户选保存路径；浏览器：<a download> 下载到默认目录。
async function doExport() {
  try {
    const res = await api.exportConnections()
    const data = res.connections || []
    const json = JSON.stringify(data, null, 2)
    const d = new Date()
    const stamp = `${d.getFullYear()}${String(d.getMonth() + 1).padStart(2, '0')}${String(d.getDate()).padStart(2, '0')}`
    const filename = `hxsfm-connections-${stamp}.json`
    if (await isDesktop()) {
      const path = await desktopSaveTextFile(filename, json)
      if (path) ElMessage.success(`已导出 ${data.length} 个连接到 ${path}`)
      return
    }
    const blob = new Blob([json], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = filename
    a.click()
    URL.revokeObjectURL(url)
    ElMessage.success(`已导出 ${data.length} 个连接（含凭据明文）`)
  } catch (e) {
    ElMessage.error(e.message)
  }
}

function pickFile() {
  fileRef.value?.click()
}

// 导入：读明文 JSON（兼容裸数组或 {connections:[...]}），先选方式再交后端。
// 去重合并：按 地址+端口+用户名+密码 四字段全一致判重，重复更新、其余新增；覆盖：清空后整体导入。
async function onFileChanged(e) {
  const file = e.target.files?.[0]
  e.target.value = '' // 允许重复选择同一文件
  if (!file) return
  importing.value = true
  try {
    const text = await file.text()
    let parsed = JSON.parse(text)
    if (parsed && Array.isArray(parsed.connections)) parsed = parsed.connections
    if (!Array.isArray(parsed)) throw new Error('文件格式不正确：应为连接数组 JSON（可由导出功能生成）')
    if (parsed.length === 0) {
      ElMessage.warning('文件中没有连接，未导入')
      return
    }

    // 选择导入方式（确认=去重合并，取消=覆盖，关闭=放弃）
    let mode = 'merge'
    try {
      await ElMessageBox.confirm(
        `已读取 ${parsed.length} 个连接，请选择导入方式。`,
        '导入连接',
        {
          confirmButtonText: '去重合并',
          cancelButtonText: '覆盖导入',
          distinguishCancelAndClose: true,
          type: 'info',
        }
      )
    } catch (action) {
      if (action !== 'cancel') return // 关闭对话框 = 放弃
      mode = 'replace'
    }

    const res = await api.importConnections(parsed, mode)
    if (mode === 'replace') {
      ElMessage.success(`覆盖导入完成：导入 ${res.replaced} 个${res.skipped ? `，跳过 ${res.skipped} 个` : ''}`)
    } else {
      const parts = [`新增 ${res.added}`, `更新 ${res.updated}`]
      if (res.skipped) parts.push(`跳过 ${res.skipped} 个`)
      ElMessage.success(`去重合并完成：${parts.join('，')}`)
    }
    await load()
  } catch (e) {
    ElMessage.error(e.message || '导入失败')
  } finally {
    importing.value = false
  }
}

// 点击「连接」：把整条已保存连接交给 App，由 App 立即开一个「正在连接…」占位 tab
// 再后台重连（与顶栏「已保存连接」下拉的 openSaved 同一套流程，行为完全一致）
function doConnect(item) {
  emit('open', item)
}

async function doDelete(item) {
  if (!confirm(`确定删除已保存的连接 “${item.name}” ?`)) return
  try {
    await api.deleteConnection(item.id)
    await load()
  } catch (e) {
    error.value = e.message
  }
}
</script>

<template>
  <div class="card saved">
    <div class="head">
      <h3 class="title">已保存的连接</h3>
      <div class="head-actions">
        <el-button size="small" text type="primary" :loading="importing" @click="doExport">
          <el-icon :size="14" style="margin-right: 4px"><Upload /></el-icon>导出
        </el-button>
        <el-button size="small" text type="primary" :loading="importing" @click="pickFile">
          <el-icon :size="14" style="margin-right: 4px"><Download /></el-icon>导入
        </el-button>
        <input
          ref="fileRef"
          type="file"
          accept=".json,application/json"
          style="display: none"
          @change="onFileChanged"
        />
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

    <el-empty
      v-if="items.length === 0"
      description="暂无保存的连接。连接成功后会自动保存到服务器。"
      :image-size="72"
    />

    <ul v-else class="list">
      <li v-for="it in items" :key="it.id" class="item">
        <div class="meta">
          <div class="name">
            {{ it.name }}
            <el-tag
              v-if="it.name !== it.host"
              size="small"
              type="info"
              effect="plain"
              class="host-tag"
            >{{ it.host }}</el-tag>
          </div>
          <div class="sub">
            {{ it.username }}@{{ it.host }}:{{ it.port }}
            <el-tag
              size="small"
              :type="it.authType === 'key' ? 'warning' : 'info'"
              effect="plain"
              style="margin-left: 6px"
            >
              {{ it.authType === 'key' ? '私钥' : '密码' }}
            </el-tag>
            <el-tag
              v-if="proxyTagInfo(it)"
              size="small"
              type="primary"
              effect="plain"
              style="margin-left: 6px"
              :title="proxyTagInfo(it).title"
            >
              {{ proxyTagInfo(it).text }}
            </el-tag>
          </div>
        </div>
        <div class="btns">
          <el-button type="primary" size="small" @click="doConnect(it)">
            连接
          </el-button>
          <el-button size="small" @click="emit('edit', it)">编辑</el-button>
          <el-button type="danger" size="small" plain @click="doDelete(it)">
            删除
          </el-button>
        </div>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.title {
  margin: 0;
  font-size: 15px;
  color: #1f2d3d;
}
.head {
  display: flex;
  align-items: center;
  margin-bottom: 12px;
}
.head-actions {
  margin-left: auto;
  display: flex;
  gap: 4px;
}
.mb {
  margin-bottom: 12px;
}
.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: #fafcff;
  transition: border-color 0.15s, box-shadow 0.15s;
}
.item:hover {
  border-color: #c9d6e8;
  box-shadow: 0 2px 8px rgba(45, 108, 223, 0.08);
}
.meta {
  flex: 1;
  min-width: 0;
}
.name {
  font-weight: 600;
  font-size: 13px;
  color: #1f2d3d;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  display: flex;
  align-items: center;
  gap: 6px;
}
.host-tag {
  flex-shrink: 0;
}
.sub {
  font-size: 12px;
  color: #7a8794;
}
.btns {
  display: flex;
  gap: 6px;
  flex-shrink: 0;
  flex-wrap: wrap;
  justify-content: flex-end;
}
</style>
