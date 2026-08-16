<script setup>
import { ref, watch, nextTick } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '../api.js'

const props = defineProps({
  connId: String,
  path: String,
})
const emit = defineEmits(['close'])

const content = ref('')
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const saved = ref(false)
// 流式读取进度（后端已改为原始字节流返回，前端边收边显示）
const progress = ref(0)
const progressText = ref('')

// 流式读取：chunk 先攒在 pendingParts，节流（~120ms）刷一次到编辑区，
// 避免大文件每个块都整段重渲染；结束后以完整内容为准
let pendingParts = []
let lastPaint = 0

function paintPartial() {
  if (!pendingParts.length) return
  content.value = pendingParts.join('')
  pendingParts = []
  lastPaint = performance.now()
}

async function loadContent() {
  if (!props.path) return
  loading.value = true
  error.value = ''
  saved.value = false
  progress.value = 0
  progressText.value = ''
  pendingParts = []
  lastPaint = 0
  content.value = ''
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
    // 最终以完整内容为准（节流期间未刷的块也会补上）
    content.value = res.content
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
    if (!error.value) {
      await nextTick(() => {
        const ta = document.querySelector('#editor-textarea textarea')
        if (ta) ta.focus()
      })
    }
  }
}

async function save() {
  saving.value = true
  error.value = ''
  saved.value = false
  try {
    await api.saveFileContent(props.connId, props.path, content.value)
    saved.value = true
    ElMessage.success('已保存')
  } catch (e) {
    error.value = e.message
  } finally {
    saving.value = false
  }
}

watch(() => props.path, loadContent, { immediate: true })
</script>

<template>
  <el-dialog
    :model-value="true"
    width="min(900px, 92vw)"
    top="6vh"
    :show-close="true"
    destroy-on-close
    @close="emit('close')"
  >
    <template #header>
      <div class="dlg-head">
        <span class="title" :title="path">{{ path }}</span>
        <span v-if="saved" class="ok">已保存 ✓</span>
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
    <el-input
      id="editor-textarea"
      v-model="content"
      type="textarea"
      :autosize="{ minRows: 18, maxRows: 34 }"
      spellcheck="false"
      resize="none"
      :disabled="loading"
      class="editor"
      @keydown.ctrl.s.prevent="save"
      @keydown.meta.s.prevent="save"
    />

    <template #footer>
      <el-button @click="emit('close')">关闭</el-button>
      <el-button type="primary" :loading="saving" @click="save">
        {{ saving ? '保存中…' : '保存 (Ctrl+S)' }}
      </el-button>
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
.mb {
  margin-bottom: 12px;
}
.loading {
  padding: 8px 0;
}
.loading-tip {
  margin-top: 8px;
  font-size: 12px;
  color: #8a97a5;
  text-align: center;
}
.editor :deep(textarea) {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 13px;
  line-height: 1.55;
  tab-size: 4;
  color: #2d3a4b;
}
</style>
