<script setup>
// 进程带宽面板：谁在占上行/下行、各多少。后端 /api/proc-net-stream 每 2 秒推一帧
// { mode, procs[], totalRxBps, totalTxBps, unaccountedRxBps, unaccountedTxBps, degraded, warmup }。
// 默认 ss 模式（仅 TCP、零依赖）；远端有 nethogs 且有权限时后端自动切 nethogs（含 UDP）。
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { openProcNetStream } from '../api.js'

const props = defineProps({
  connId: String,
  visible: Boolean,
})

let es = null
const live = ref(null)
const err = ref('')
// 速率会每帧抖动，按 pid 做短 EMA 平滑（α=0.4），否则按速率排序时行来回乱跳没法读
const smooth = new Map() // pid -> { rx, tx }
const rows = ref([])

function formatBytes(n) {
  if (n == null || Number.isNaN(n) || n < 0) return '—'
  const u = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']
  let i = 0
  let v = Number(n)
  while (v >= 1024 && i < u.length - 1) {
    v /= 1024
    i++
  }
  return `${v < 10 && i > 0 ? v.toFixed(1) : Math.round(v)} ${u[i]}`
}
function formatRate(bps) {
  return formatBytes(bps) + '/s'
}

const ALPHA = 0.4
function applyFrame(msg) {
  if (msg?.error === 'remote-missing-ss') {
    err.value = '远端未安装 ss（iproute2），也没有 nethogs —— 无法采集进程带宽'
    rows.value = []
    return
  }
  err.value = ''
  live.value = msg
  const seen = new Set()
  const next = []
  for (const p of msg.procs || []) {
    seen.add(p.pid)
    const prev = smooth.get(p.pid) || { rx: p.rxRateBps, tx: p.txRateBps }
    const rx = prev.rx + ALPHA * (p.rxRateBps - prev.rx)
    const tx = prev.tx + ALPHA * (p.txRateBps - prev.tx)
    smooth.set(p.pid, { rx, tx })
    next.push({ ...p, rx, tx })
  }
  // 清掉本帧没出现的 pid，避免 Map 无限增长
  for (const pid of [...smooth.keys()]) if (!seen.has(pid)) smooth.delete(pid)
  next.sort((a, b) => b.rx + b.tx - (a.rx + a.tx))
  rows.value = next
}

function start() {
  stop()
  if (!props.connId) return
  err.value = ''
  live.value = null
  smooth.clear()
  rows.value = []
  es = openProcNetStream(props.connId, applyFrame, 2)
}
function stop() {
  if (es) {
    es.close()
    es = null
  }
}

// 只在面板可见且有连接时才建流；关掉即断流（ss 在海量 socket 机器上不便宜）
watch(
  () => [props.visible, props.connId],
  ([vis, id]) => {
    if (vis && id) start()
    else stop()
  },
  { immediate: true },
)
onBeforeUnmount(stop)

const modeLabel = computed(() => {
  const m = live.value?.mode
  if (m === 'nethogs') return 'nethogs 精确模式 · 含 UDP'
  if (m === 'ss') return 'ss 模式 · 仅 TCP（UDP/QUIC 计入未归属）'
  return ''
})
const degradedNote = computed(() => {
  const d = live.value?.degraded
  if (!d) return ''
  const notes = []
  if (d.noPid) notes.push('非 root：仅当前用户进程可见')
  if (d.sudoUsed) notes.push('已用免密 sudo 提权采集')
  return notes.join(' · ')
})
const warmup = computed(() => live.value?.warmup)
const totalRx = computed(() => live.value?.totalRxBps || 0)
const totalTx = computed(() => live.value?.totalTxBps || 0)
const unRx = computed(() => live.value?.unaccountedRxBps || 0)
const unTx = computed(() => live.value?.unaccountedTxBps || 0)
</script>

<template>
  <div class="pn">
    <div class="pn-head">
      <div class="pn-totals">
        <span class="pn-total down">↓ 下行 {{ formatRate(totalRx) }}</span>
        <span class="pn-total up">↑ 上行 {{ formatRate(totalTx) }}</span>
      </div>
      <span v-if="modeLabel" class="pn-mode">{{ modeLabel }}</span>
    </div>

    <div v-if="degradedNote" class="pn-degraded">{{ degradedNote }}</div>
    <div v-if="err" class="pn-missing">
      <el-alert :title="err" type="warning" :closable="false" show-icon />
      <div class="pn-install">
        <div class="pn-install-title">在远端安装后即可采集（装其一即可，nethogs 还能覆盖 UDP）：</div>
        <div class="pn-install-row">
          <span class="pn-install-os">Debian / Ubuntu</span>
          <code>sudo apt install -y iproute2 nethogs</code>
        </div>
        <div class="pn-install-row">
          <span class="pn-install-os">CentOS / RHEL 7</span>
          <code>sudo yum install -y iproute nethogs</code>
        </div>
        <div class="pn-install-row">
          <span class="pn-install-os">RHEL 8+ / Fedora</span>
          <code>sudo dnf install -y iproute nethogs</code>
        </div>
        <div class="pn-install-row">
          <span class="pn-install-os">Alpine</span>
          <code>sudo apk add iproute2 nethogs</code>
        </div>
        <div class="pn-install-row">
          <span class="pn-install-os">openSUSE</span>
          <code>sudo zypper install -y iproute2 nethogs</code>
        </div>
        <div class="pn-install-note">nethogs 属 EPEL 源，CentOS/RHEL 上可能需先 <code>sudo yum install -y epel-release</code>。装完重新打开本面板即可。</div>
      </div>
    </div>
    <div v-else-if="warmup" class="pn-warm">采样中…（首帧只建立基线，第二帧起显示速率）</div>

    <el-table
      v-if="!err"
      :data="rows"
      size="small"
      max-height="360"
      empty-text="暂无进程占用（或采样中）"
    >
      <el-table-column prop="comm" label="进程" min-width="160" show-overflow-tooltip>
        <template #default="{ row }">
          <span class="pn-comm">{{ row.comm }}</span>
        </template>
      </el-table-column>
      <el-table-column prop="pid" label="PID" width="90" />
      <el-table-column label="下行" min-width="110" sortable :sort-by="(r) => r.rx">
        <template #default="{ row }">
          <span class="pn-rate down">↓ {{ formatRate(row.rx) }}</span>
        </template>
      </el-table-column>
      <el-table-column label="上行" min-width="110" sortable :sort-by="(r) => r.tx">
        <template #default="{ row }">
          <span class="pn-rate up">↑ {{ formatRate(row.tx) }}</span>
        </template>
      </el-table-column>
      <el-table-column prop="conns" label="连接" width="80" />
    </el-table>

    <!-- 未归属：网卡总量 − 明细之和。ss 模式下这里主要是 UDP/QUIC/内核态/无权限看到的部分 -->
    <div v-if="!err && (unRx > 0 || unTx > 0)" class="pn-un">
      <span class="pn-un-label">其他 / 未归属</span>
      <span class="pn-rate down">↓ {{ formatRate(unRx) }}</span>
      <span class="pn-rate up">↑ {{ formatRate(unTx) }}</span>
      <span class="pn-un-hint">UDP / QUIC / 内核态 / 已关闭连接{{ live?.degraded?.noPid ? ' / 他人进程' : '' }}</span>
    </div>
  </div>
</template>

<style scoped>
.pn {
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.pn-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}
.pn-totals {
  display: flex;
  gap: 16px;
}
.pn-total {
  font-size: 15px;
  font-weight: 600;
  font-family: ui-monospace, monospace;
}
.pn-total.down {
  color: #409eff;
}
.pn-total.up {
  color: #67c23a;
}
.pn-mode {
  font-size: 12px;
  color: #909399;
}
.pn-degraded {
  font-size: 12px;
  color: #e6a23c;
}
.pn-err,
.pn-warm {
  font-size: 12px;
}
.pn-warm {
  color: #909399;
}
.pn-missing {
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.pn-install {
  border: 1px solid #ebeef5;
  border-radius: 4px;
  padding: 12px 14px;
  background: #fafcff;
}
.pn-install-title {
  font-size: 12px;
  color: #606266;
  margin-bottom: 10px;
}
.pn-install-row {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 6px;
}
.pn-install-os {
  flex: 0 0 120px;
  font-size: 12px;
  color: #909399;
}
.pn-install code,
.pn-install-note code {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  background: #f2f5fa;
  border: 1px solid #e6eaf0;
  border-radius: 3px;
  padding: 2px 6px;
  color: #2d3a4b;
  user-select: all;
}
.pn-install-note {
  margin-top: 8px;
  font-size: 11px;
  color: #a8b3c0;
  line-height: 1.6;
}
.pn-comm {
  font-family: ui-monospace, monospace;
}
.pn-rate {
  font-family: ui-monospace, monospace;
  font-size: 13px;
}
.pn-rate.down {
  color: #409eff;
}
.pn-rate.up {
  color: #67c23a;
}
.pn-un {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 8px 12px;
  background: #f5f7fa;
  border-radius: 4px;
  font-size: 13px;
}
.pn-un-label {
  font-weight: 600;
  color: #606266;
}
.pn-un-hint {
  font-size: 11px;
  color: #a8b3c0;
  margin-left: auto;
}
</style>
