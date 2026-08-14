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

async function loadContent() {
  if (!props.path) return
  loading.value = true
  error.value = ''
  saved.value = false
  try {
    const res = await api.getFileContent(props.connId, props.path)
    content.value = res.content
    await nextTick(() => {
      const ta = document.querySelector('#editor-textarea textarea')
      if (ta) ta.focus()
    })
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
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
      <el-skeleton :rows="6" animated />
    </div>
    <el-input
      v-else
      id="editor-textarea"
      v-model="content"
      type="textarea"
      :autosize="{ minRows: 18, maxRows: 34 }"
      spellcheck="false"
      resize="none"
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
.editor :deep(textarea) {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 13px;
  line-height: 1.55;
  tab-size: 4;
  color: #2d3a4b;
}
</style>
