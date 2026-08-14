<script setup>
import { ref, nextTick } from 'vue'
import { api } from '../api.js'

// cwd：会话当前工作目录（App 持有，跨命令保留）；命令返回新 cwd 后 emit 给 App 联动文件列表
const props = defineProps({
  connId: String,
  cwd: { type: String, default: '/' },
})
const emit = defineEmits(['update:cwd'])

const command = ref('')
const lines = ref([])
const busy = ref(false)
const inputRef = ref(null)

// 命令执行后恢复输入框焦点（busy 不再禁用输入框，焦点不会被浏览器夺走）
function restoreFocus() {
  nextTick(() => inputRef.value?.focus())
}

function push(type, text) {
  String(text ?? '').split('\n').forEach((l) => lines.value.push({ type, text: l }))
}

async function run() {
  const cmd = command.value
  if (!cmd.trim() || busy.value) return
  push('cmd', '$ ' + cmd)
  command.value = ''
  busy.value = true
  try {
    const res = await api.runCommand(props.connId, cmd)
    if (res.output) push('out', res.output)
    if (res.error) push('err', res.error)
    push('meta', `exit=${res.exitStatus}`)
    if (res.cwd) emit('update:cwd', res.cwd)
  } catch (e) {
    push('err', e.message)
  } finally {
    busy.value = false
    restoreFocus()
    await nextTick()
    const box = document.getElementById('termOut')
    if (box) box.scrollTop = box.scrollHeight
  }
}
</script>

<template>
  <div class="card term">
    <div class="term-head">
      <h3 class="title">命令终端</h3>
      <el-button
        size="small"
        text
        :disabled="lines.length === 0"
        @click="lines = []"
      >
        清空
      </el-button>
    </div>

    <div id="termOut" class="out">
      <div v-for="(l, i) in lines" :key="i" class="line" :class="l.type">
        {{ l.text }}
      </div>
      <p v-if="lines.length === 0" class="hint">
        输入命令，回车执行。例如 ls -la
      </p>
    </div>

    <div class="prompt">
      <span class="prompt-path" :title="cwd">{{ cwd || '/' }}</span>
      <span class="sigil">$</span>
      <el-input
        ref="inputRef"
        v-model="command"
        placeholder="输入命令，回车执行，例如 ls -la"
        autocomplete="off"
        clearable
        @keyup.enter="run"
      >
        <template #append>
          <el-button :loading="busy" @click="run">执行</el-button>
        </template>
      </el-input>
    </div>
  </div>
</template>

<style scoped>
.term {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}
.term-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 10px;
}
.title {
  margin: 0;
  font-size: 15px;
  color: #1f2d3d;
}
.out {
  flex: 1;
  min-height: 0;
  overflow: auto;
  background: #0f1620;
  border-radius: 10px;
  padding: 10px 12px;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12.5px;
  line-height: 1.5;
}
.line {
  white-space: pre-wrap;
  word-break: break-all;
}
.line.cmd {
  color: #7fd1ff;
}
.line.out {
  color: #d6e2ef;
}
.line.err {
  color: #ff8a8a;
}
.line.meta {
  color: #8a97a5;
}
.hint {
  color: #56606c;
  margin: 4px 0;
}
.prompt {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 8px;
}
.prompt-path {
  flex-shrink: 1;
  min-width: 0;
  max-width: 40%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: #2ecc71;
  font-family: ui-monospace, monospace;
  font-size: 12.5px;
}
.sigil {
  color: #2ecc71;
  font-family: ui-monospace, monospace;
  font-weight: 700;
}
.prompt :deep(.el-input) {
  flex: 1;
}
.prompt :deep(.el-input-group__append) {
  background: #0f1620;
}
.prompt :deep(.el-input-group__append .el-button) {
  color: #d6e2ef;
}
</style>
