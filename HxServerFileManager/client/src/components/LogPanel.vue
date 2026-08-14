<script setup>
import { ref, onMounted, onUnmounted } from 'vue'
import { openLogStream } from '../api.js'

const entries = ref([])
const seen = new Set()
let es = null

function start() {
  stop()
  entries.value = []
  seen.clear()
  es = openLogStream((e) => {
    const key = `${e.Time}|${e.Connection}|${e.Action}|${e.Detail}|${e.Result}`
    if (seen.has(key)) return
    seen.add(key)
    entries.value.push(e)
    if (entries.value.length > 500) entries.value.shift()
    scroll()
  })
}
function stop() {
  if (es) {
    es.close()
    es = null
  }
}
function scroll() {
  requestAnimationFrame(() => {
    const box = document.getElementById('logBox')
    if (box) box.scrollTop = box.scrollHeight
  })
}

onMounted(start)
onUnmounted(stop)
</script>

<template>
  <div class="logpanel">
    <div class="log-head">
      <span class="log-title">📜 实时操作日志</span>
      <el-tag size="small" type="info" effect="dark" round>{{ entries.length }}</el-tag>
    </div>
    <div id="logBox" class="log-box">
      <div v-for="(e, i) in entries" :key="i" class="log-line" :class="'lv-' + (e.Level || 'info')">
        <span class="t">{{ e.Time }}</span>
        <span class="lv">{{ e.Level }}</span>
        <span class="act">{{ e.Action }}</span>
        <span class="conn">{{ e.Connection }}</span>
        <span class="detail">{{ e.Detail }}<template v-if="e.Result"> · {{ e.Result }}</template></span>
      </div>
      <p v-if="entries.length === 0" class="empty">等待操作…</p>
    </div>
  </div>
</template>

<style scoped>
.logpanel {
  height: 190px;
  background: #0f1620;
  color: #d6e2ef;
  display: flex;
  flex-direction: column;
  border-top: 1px solid #1d2733;
  flex-shrink: 0;
}
.log-head {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 14px;
  font-size: 13px;
  color: #9fb3c8;
  border-bottom: 1px solid #1d2733;
}
.log-box {
  flex: 1;
  overflow: auto;
  padding: 6px 14px;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12px;
  line-height: 1.6;
}
.log-line {
  display: flex;
  gap: 8px;
  white-space: pre-wrap;
  word-break: break-all;
}
.log-line .t {
  color: #6f8094;
  flex-shrink: 0;
}
.log-line .lv {
  flex-shrink: 0;
  width: 46px;
}
.log-line .act {
  flex-shrink: 0;
  width: 70px;
  color: #7fd1ff;
}
.log-line .conn {
  flex-shrink: 0;
  color: #8a97a5;
}
.lv-info .lv {
  color: #7fd1ff;
}
.lv-error .lv {
  color: #ff8a8a;
}
.lv-error {
  color: #ffd2d2;
}
.empty {
  color: #6f8094;
}
</style>
