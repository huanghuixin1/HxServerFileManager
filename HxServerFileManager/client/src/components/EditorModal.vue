<script setup>
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '../api.js'

const props = defineProps({
  connId: String,
  path: String,
})
const emit = defineEmits(['close'])

const host = ref(null) // CodeMirror 挂载点
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const saved = ref(false)
const dirty = ref(false)
const wrap = ref(false)
const lines = ref(0)
// 流式读取进度（后端返回原始字节流，前端边收边填进编辑器）
const progress = ref(0)
const progressText = ref('')

// CodeMirror handle（见 src/cmEditor.js）。故意不放 ref：编辑器实例不需要响应式代理
let cm = null
// 流式 chunk 先攒着，节流 ~120ms 刷一次，避免每个块都单独发一次事务
let pendingParts = []
let lastPaint = 0

function paintPartial() {
  if (!pendingParts.length || !cm) return
  cm.append(pendingParts.join(''))
  pendingParts = []
  lastPaint = performance.now()
  lines.value = cm.lines()
}

// 编辑器走动态 import：不打开这个弹窗就不会下载 CodeMirror 那个 chunk
async function ensureEditor() {
  if (cm) return cm
  const { createEditor } = await import('../cmEditor.js')
  if (!host.value) return null // 等 import 的间隙里弹窗被关了
  cm = createEditor({
    parent: host.value,
    readOnly: true, // 加载期只读，读完再解锁
    onSave: () => { if (!loading.value && !saving.value) save() },
    onChange: () => { dirty.value = true; saved.value = false; lines.value = cm.lines() },
  })
  cm.setWrap(wrap.value)
  return cm
}

async function loadContent() {
  if (!props.path) return
  loading.value = true
  error.value = ''
  saved.value = false
  dirty.value = false
  progress.value = 0
  progressText.value = ''
  pendingParts = []
  lastPaint = 0
  const ed = await ensureEditor()
  if (!ed) {
    loading.value = false
    return
  }
  ed.setDoc('')
  ed.setReadOnly(true)
  ed.setLanguage(props.path) // 按扩展名挑高亮，取不到就纯文本（不 await，不挡加载）
  try {
    const res = await api.getFileContent(props.connId, props.path, ({ loaded, total, percent, chunk }) => {
      progress.value = percent
      const mb = (n) => (n / 1024 / 1024).toFixed(1)
      progressText.value = total
        ? `读取中… ${percent}%（${mb(loaded)} / ${mb(total)} MB）`
        : `读取中… ${mb(loaded)} MB`
      pendingParts.push(chunk)
      if (performance.now() - lastPaint > 120) paintPartial()
    })
    // 最终以完整内容为准（节流期间没刷出去的块也一并补上）
    ed.setDoc(res.content)
    lines.value = ed.lines()
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
    if (!error.value) {
      ed.setReadOnly(false)
      ed.focus()
    }
  }
}

async function save() {
  if (!cm || loading.value) return
  saving.value = true
  error.value = ''
  saved.value = false
  try {
    await api.saveFileContent(props.connId, props.path, cm.getDoc())
    saved.value = true
    dirty.value = false
    ElMessage.success('已保存')
  } catch (e) {
    error.value = e.message
  } finally {
    saving.value = false
  }
}

function openSearch() {
  cm?.openSearch()
}

watch(wrap, (v) => cm?.setWrap(v))
// 组件由父级 v-if 创建/销毁，路径在挂载时就已就绪；挂载后再建编辑器（此时 host 才有 DOM）
onMounted(loadContent)
watch(() => props.path, loadContent)
onBeforeUnmount(() => {
  cm?.destroy()
  cm = null
})

</script>

<template>
  <el-dialog
    :model-value="true"
    width="min(1100px, 94vw)"
    top="6vh"
    :show-close="true"
    destroy-on-close
    :close-on-click-modal="false"
    @close="emit('close')"
  >
    <template #header>
      <div class="dlg-head">
        <span class="title" :title="path">{{ path }}</span>
        <span v-if="dirty" class="warn">● 未保存</span>
        <span v-else-if="saved" class="ok">已保存 ✓</span>
      </div>
    </template>

    <el-alert
      v-if="error"
      :title="error"
      type="error"
      :closable="false"
      show-icon
      class="mb"
    />

    <div v-if="loading" class="loading">
      <el-progress :percentage="progress" :stroke-width="6" :show-text="false" />
      <div class="loading-tip">{{ progressText }}</div>
    </div>

    <!-- CodeMirror 挂在这里；高度固定，滚动交给编辑器自己（不要再套 autosize） -->
    <div ref="host" class="editor-host"></div>

    <template #footer>
      <div class="foot">
        <div class="foot-left">
          <el-button size="small" :disabled="loading" @click="openSearch">查找 / 替换</el-button>
          <el-checkbox v-model="wrap" size="small" :disabled="loading">自动换行</el-checkbox>
          <span class="hint">{{ lines }} 行 · Ctrl+F 查找 · Alt+G 跳转行 · Ctrl+S 保存</span>
        </div>
        <div class="foot-right">
          <el-button @click="emit('close')">关闭</el-button>
          <el-button type="primary" :loading="saving" :disabled="loading" @click="save">
            {{ saving ? '保存中…' : '保存 (Ctrl+S)' }}
          </el-button>
        </div>
      </div>
    </template>
  </el-dialog>
</template>

<style scoped>
.dlg-head {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}
.title {
  flex: 1;
  min-width: 0;
  font-size: 13px;
  color: #1f2d3d;
  font-family: ui-monospace, monospace;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ok {
  color: #2ecc71;
  font-size: 12px;
  flex-shrink: 0;
}
.warn {
  color: #e6a23c;
  font-size: 12px;
  flex-shrink: 0;
}
.mb {
  margin-bottom: 12px;
}
.loading {
  padding: 0 0 8px;
}
.loading-tip {
  margin-top: 8px;
  font-size: 12px;
  color: #8a97a5;
  text-align: center;
}
/* 编辑器容器：固定高度 + overflow hidden，内部滚动由 CodeMirror 的 .cm-scroller 负责 */
.editor-host {
  height: 62vh;
  border: 1px solid #dcdfe6;
  border-radius: 4px;
  overflow: hidden;
}
.foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
.foot-left {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}
.hint {
  font-size: 12px;
  color: #a0acb9;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.foot-right {
  flex-shrink: 0;
}
</style>
