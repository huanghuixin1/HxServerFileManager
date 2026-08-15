<script setup>
import { ref, computed, watch, onUnmounted } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '../api.js'

const props = defineProps({
  connId: String,
})

const status = ref(null)
const loading = ref(false)
const detailVisible = ref(false) // 「详情」弹窗
const auto = ref(true)            // 迷你状态栏自动刷新
const updatedAt = ref(null)
let timer = null
let prevNetSnap = null // 上一次的网络采样，用于计算实时上行/下行速率

// ---- 格式化辅助 ----
function formatBytes(n) {
  if (n == null || Number.isNaN(n) || n < 0) return '—'
  const u = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']
  let i = 0
  let v = Number(n)
  while (v >= 1024 && i < u.length - 1) {
    v /= 1024
    i++
  }
  const digits = i === 0 ? 0 : v >= 10 ? 1 : 2
  return `${v.toFixed(digits)} ${u[i]}`
}

function formatKb(kb) {
  return formatBytes((kb || 0) * 1024)
}

// 迷你条用紧凑格式：Xd Xh 或 Xd Xh Xm
function shortUptime(sec) {
  const n = Number(sec) || 0
  if (n <= 0) return '—'
  const d = Math.floor(n / 86400)
  const h = Math.floor((n % 86400) / 3600)
  const m = Math.floor((n % 3600) / 60)
  if (d > 0) return `${d}d ${h}h`
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

// 详情弹窗用完整格式
function fullUptime(sec) {
  const n = Number(sec) || 0
  if (n <= 0) return '—'
  const d = Math.floor(n / 86400)
  const h = Math.floor((n % 86400) / 3600)
  const m = Math.floor((n % 3600) / 60)
  const parts = []
  if (d) parts.push(`${d} 天`)
  if (h || d) parts.push(`${h} 小时`)
  parts.push(`${m} 分钟`)
  return parts.join(' ')
}

function bootTime(unixTs, uptime) {
  const ts = Number(unixTs) || 0
  const up = Number(uptime) || 0
  if (!ts || !up) return ''
  const d = new Date((ts - up) * 1000)
  if (Number.isNaN(d.getTime())) return ''
  const pad = (x) => String(x).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`
}

function usageColor(p) {
  if (p == null || Number.isNaN(p)) return 'var(--border)'
  if (p >= 90) return '#e74c3c'
  if (p >= 75) return '#e67e22'
  return '#2d6cdf'
}

function parseUse(s) {
  const n = parseFloat(String(s || '').replace('%', ''))
  return Number.isNaN(n) ? 0 : n
}

function netStateType(st) {
  if (st === 'up') return 'success'
  if (st === 'down') return 'info'
  return 'warning'
}

function netStateLabel(st) {
  if (st === 'up') return '已连接'
  if (st === 'down') return '未连接'
  return st || '未知'
}

// mini 条显示的迷你进度条宽度（百分比 0-100）
function barWidth(p) {
  if (p == null || Number.isNaN(p)) return 0
  return Math.min(100, Math.max(0, Number(p)))
}

// ---- 采集 ----
async function load() {
  if (!props.connId) return
  loading.value = true
  try {
    status.value = await api.systemStatus(props.connId)
    updatedAt.value = new Date()
    // 网络速率：基于两次采样的字节差 / 时间差（bytes/s）
    const nets = status.value?.nets || []
    const now = Date.now()
    if (prevNetSnap && now - prevNetSnap.ts > 800) {
      const dt = (now - prevNetSnap.ts) / 1000
      for (const n of nets) {
        const p = prevNetSnap.nets.find((x) => x.name === n.name)
        n.rxRateBps = (p && n.rxBytes >= p.rx && n.txBytes >= p.tx)
          ? Math.max(0, (n.rxBytes - p.rx) / dt)
          : 0
        n.txRateBps = (p && n.rxBytes >= p.rx && n.txBytes >= p.tx)
          ? Math.max(0, (n.txBytes - p.tx) / dt)
          : 0
      }
    } else {
      for (const n of nets) { /* 首次无差值，速率记 0 */ n.rxRateBps = 0; n.txRateBps = 0 }
    }
    prevNetSnap = { ts: now, nets: nets.map((n) => ({ name: n.name, rx: n.rxBytes, tx: n.txBytes })) }
  } catch (e) {
    ElMessage.error(e.message || '获取服务器状态失败')
  } finally {
    loading.value = false
  }
}

function startAuto() {
  stopAuto()
  if (!auto.value || !props.connId) return
  timer = setInterval(load, 10000) // 常驻条 10s 一次，省得频繁打 SSH
}

function stopAuto() {
  if (timer) {
    clearInterval(timer)
    timer = null
  }
}

watch(
  () => props.connId,
  (id) => {
    if (id) {
      load()
      startAuto()
    } else {
      stopAuto()
      status.value = null
    }
  },
  { immediate: true }
)

watch(auto, startAuto)

// 打开详情弹窗时立即刷新一次
watch(detailVisible, (open) => {
  if (open) load()
})

onUnmounted(stopAuto)

// ---- mini 条展示值 ----
const shortOs = computed(() => {
  const os = status.value?.os || ''
  return os.length > 26 ? os.slice(0, 26) + '…' : os
})

// 迷你条磁盘区：过滤掉虚拟/swap 分区，优先根分区靠前，最多显示 3 个挂载盘
const mainDisks = computed(() => {
  const disks = status.value?.disks || []
  const real = disks.filter((d) => {
    const fs = String(d.fs || '')
    const m = String(d.mount || '')
    if (m === '/dev/shm') return false
    if (/^(tmpfs|devtmpfs|squashfs|udev|ramfs|shm|proc|sysfs)/.test(fs)) return false
    return true
  })
  const list = real.length ? real : disks
  return [...list].sort((a, b) => (a.mount === '/' ? -1 : b.mount === '/' ? 1 : 0)).slice(0, 3)
})

const netSummary = computed(() => {
  const nets = status.value?.nets || []
  const up = nets.filter((n) => n.state === 'up').length
  if (!nets.length) return ''
  const down = nets.reduce((s, n) => s + (n.rxRateBps || 0), 0)
  const up_ = nets.reduce((s, n) => s + (n.txRateBps || 0), 0)
  // 首次采样无速率差，仅显示接口数
  if (!down && !up_) return `${up}/${nets.length} 网卡`
  return `↓ ${formatRate(down)} ↑ ${formatRate(up_)}`
})

function formatRate(bps) {
  return formatBytes(bps) + '/s'
}
</script>

<template>
  <!-- ====== 底部迷你状态栏（MobaXterm 风格） ====== -->
  <div class="ss-bar" title="点击查看服务器状态详情">
    <div class="ss-item host" @click="detailVisible = true">
      <el-icon :size="13"><Monitor /></el-icon>
      <span class="host">{{ status?.hostname || '服务器' }}</span>
    </div>

    <div class="ss-item" title="系统版本" @click="detailVisible = true">
      <span class="lbl">OS</span>
      <span class="val">{{ shortOs || '—' }}</span>
    </div>

    <div class="ss-item" title="开机时间" @click="detailVisible = true">
      <span class="lbl">开机</span>
      <span class="val mono">{{ shortUptime(status?.uptimeSeconds) }}</span>
    </div>

    <div class="ss-item" title="CPU 占用" @click="detailVisible = true">
      <span class="lbl">CPU</span>
      <span class="val mono" :style="{ color: usageColor(status?.cpuPercent) }">
        {{ status?.cpuPercent == null ? '—' : status.cpuPercent.toFixed(0) + '%' }}
      </span>
      <span class="mini-bar">
        <i :style="{ width: barWidth(status?.cpuPercent) + '%', background: usageColor(status?.cpuPercent) }"></i>
      </span>
    </div>

    <div class="ss-item" title="内存占用" @click="detailVisible = true">
      <span class="lbl">内存</span>
      <span class="val mono" :style="{ color: usageColor(status?.memPercent) }">
        {{ status?.memPercent == null ? '—' : status.memPercent.toFixed(0) + '%' }}
      </span>
      <span class="mini-bar">
        <i :style="{ width: barWidth(status?.memPercent) + '%', background: usageColor(status?.memPercent) }"></i>
      </span>
    </div>

    <div
      v-for="d in mainDisks"
      :key="d.mount"
      class="ss-item"
      :title="`磁盘 ${d.mount}（${d.fs}）${d.use} · 已用 ${d.used} / 总 ${d.size} / 可用 ${d.avail}`"
      @click="detailVisible = true"
    >
      <span class="lbl">{{ d.mount }}</span>
      <span class="val mono" :style="{ color: usageColor(parseUse(d.use)) }">{{ d.use }}</span>
      <span class="val size">{{ d.size }}</span>
      <span class="mini-bar">
        <i :style="{ width: barWidth(parseUse(d.use)) + '%', background: usageColor(parseUse(d.use)) }"></i>
      </span>
    </div>

    <div v-if="netSummary" class="ss-item" title="网络上下行速率（10 秒窗口平均）" @click="detailVisible = true">
      <span class="lbl">网络</span>
      <span class="val mono">{{ netSummary }}</span>
    </div>

    <div class="ss-spacer"></div>

    <span v-if="updatedAt" class="ss-ts" :title="'更新时间 ' + updatedAt.toLocaleTimeString()">
      {{ updatedAt.toLocaleTimeString() }}
    </span>
    <el-button size="small" text type="primary" :loading="loading" @click="load">
      <el-icon style="margin-right: 3px"><Refresh /></el-icon>
    </el-button>
    <el-button size="small" type="primary" plain @click="detailVisible = true">
      详情
    </el-button>
  </div>

  <!-- ====== 详情弹窗（完整数据） ====== -->
  <el-dialog
    v-model="detailVisible"
    width="min(920px, 94vw)"
    :close-on-click-modal="false"
  >
    <template #header>
      <div class="ss-dlg-head">
        <span class="ss-dlg-title">服务器状态</span>
        <span v-if="status?.hostname" class="ss-host">{{ status.hostname }}</span>
      </div>
    </template>

    <div v-loading="loading && !status" class="ss">
      <p v-if="!status && !loading" class="ss-empty">暂无数据，点刷新采集当前服务器状态</p>
      <template v-else-if="status">
          <div class="ss-grid">
            <div class="ss-card">
              <div class="ss-card-label">
                <el-icon><Monitor /></el-icon>系统版本
              </div>
              <div class="ss-value">{{ status.os || '—' }}</div>
              <div class="ss-sub">
                {{ [status.kernel, status.arch].filter(Boolean).join(' · ') || '内核信息不可用' }}
              </div>
            </div>

            <div class="ss-card">
              <div class="ss-card-label">
                <el-icon><Timer /></el-icon>开机时间
              </div>
              <div class="ss-value">{{ fullUptime(status.uptimeSeconds) }}</div>
              <div class="ss-sub">
                {{ bootTime(status.unixTs, status.uptimeSeconds) ? `启动于 ${bootTime(status.unixTs, status.uptimeSeconds)}` : '启动时刻不可用' }}
              </div>
            </div>

            <div class="ss-card">
              <div class="ss-card-label">
                <el-icon><Cpu /></el-icon>CPU 占用
              </div>
              <div class="ss-value">
                {{ status.cpuPercent == null ? '—' : status.cpuPercent.toFixed(1) + '%' }}
              </div>
              <el-progress
                :percentage="Math.min(100, Math.max(0, status.cpuPercent ?? 0))"
                :color="usageColor(status.cpuPercent)"
                :show-text="false"
                :stroke-width="8"
              />
            </div>

            <div class="ss-card">
              <div class="ss-card-label">
                <el-icon><Coin /></el-icon>内存占用
              </div>
              <div class="ss-value">{{ status.memPercent.toFixed(1) }}%</div>
              <el-progress
                :percentage="Math.min(100, Math.max(0, status.memPercent))"
                :color="usageColor(status.memPercent)"
                :show-text="false"
                :stroke-width="8"
              />
              <div class="ss-sub">
                {{ formatKb(status.memUsed) }} / {{ formatKb(status.memTotal) }}
                <template v-if="status.swapTotal">
                  · swap {{ formatKb(status.swapUsed) }} / {{ formatKb(status.swapTotal) }}
                  ({{ status.swapPercent.toFixed(1) }}%)
                </template>
              </div>
            </div>
          </div>

          <div class="ss-block">
            <div class="ss-card-label">
              <el-icon><FolderOpened /></el-icon>磁盘
            </div>
            <el-table :data="status.disks" empty-text="未采集到磁盘信息" size="small" max-height="220">
              <el-table-column prop="mount" label="挂载点" min-width="120" />
              <el-table-column prop="fs" label="文件系统" min-width="120" show-overflow-tooltip />
              <el-table-column prop="size" label="容量" width="80" />
              <el-table-column prop="used" label="已用" width="80" />
              <el-table-column prop="avail" label="可用" width="80" />
              <el-table-column label="占用" width="150">
                <template #default="{ row }">
                  <div class="ss-use">
                    <el-progress
                      :percentage="parseUse(row.use)"
                      :color="usageColor(parseUse(row.use))"
                      :stroke-width="8"
                    />
                  </div>
                </template>
              </el-table-column>
            </el-table>
          </div>

          <div class="ss-block">
            <div class="ss-card-label">
              <el-icon><Connection /></el-icon>网络状态
            </div>
            <el-table :data="status.nets" empty-text="未采集到网卡信息（容器内可能无 /proc/net/dev）" size="small" max-height="200">
              <el-table-column prop="name" label="网卡" width="140" />
              <el-table-column label="状态" width="100">
                <template #default="{ row }">
                  <el-tag :type="netStateType(row.state)" size="small" effect="light" round>
                    {{ netStateLabel(row.state) }}
                  </el-tag>
                </template>
              </el-table-column>
              <el-table-column label="下行速度" min-width="120">
                <template #default="{ row }">
                  <div class="net-cell">
                    <span class="net-rate">↓ {{ formatRate(row.rxRateBps || 0) }}</span>
                    <span class="net-total">累计 {{ formatBytes(row.rxBytes) }}</span>
                  </div>
                </template>
              </el-table-column>
              <el-table-column label="上行速度" min-width="120">
                <template #default="{ row }">
                  <div class="net-cell">
                    <span class="net-rate">↑ {{ formatRate(row.txRateBps || 0) }}</span>
                    <span class="net-total">累计 {{ formatBytes(row.txBytes) }}</span>
                  </div>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </template>
    </div>

    <template #footer>
      <el-checkbox v-model="auto">自动刷新（10 秒）</el-checkbox>
      <div class="ss-spacer"></div>
      <el-button :loading="loading" @click="load">刷新</el-button>
      <el-button type="primary" @click="detailVisible = false">关闭</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
/* ====== 底部迷你状态栏 ====== */
.ss-bar {
  display: flex;
  align-items: center;
  gap: 14px;
  height: 34px;
  padding: 0 14px;
  background: #f7f9fc;
  border-top: 1px solid var(--border);
  font-size: 12px;
  color: #5b6b7b;
  flex-shrink: 0;
  user-select: none;
}
.ss-item {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 8px;
  border-radius: 6px;
  cursor: pointer;
  white-space: nowrap;
  transition: background 0.15s;
}
.ss-item:hover {
  background: #eef3fa;
}
.ss-item .host {
  font-weight: 600;
  color: #2d6cdf;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}
.ss-item .lbl {
  color: #8a97a5;
}
.ss-item .val {
  color: #3d4b5c;
}
.ss-item .val.size {
  color: #a0abba;
  font-size: 11px;
}
.ss-item .val.mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-weight: 600;
}
/* 迷你进度条：容器固定宽，内部 i 按百分比撑宽 */
.mini-bar {
  display: inline-block;
  width: 56px;
  height: 6px;
  border-radius: 3px;
  background: #e3e8ef;
  overflow: hidden;
  flex-shrink: 0;
}
.mini-bar i {
  display: block;
  height: 100%;
  border-radius: 3px;
  transition: width 0.3s ease;
}
.ss-spacer {
  flex: 1;
}
.ss-ts {
  color: #a0abba;
  font-size: 11.5px;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
}

/* ====== 详情弹窗 ====== */
.ss-dlg-head {
  display: flex;
  align-items: center;
  gap: 10px;
}
.ss-dlg-title {
  font-weight: 700;
  font-size: 16px;
  color: #1f2d3d;
}
.ss-host {
  color: #2d6cdf;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 13px;
}
.ss {
  min-height: 160px;
}
/* 弹窗 footer：自动刷新勾选靠左，按钮靠右 */
:deep(.el-dialog__footer) {
  display: flex;
  align-items: center;
  gap: 12px;
}
.ss-empty {
  color: #8a97a5;
  text-align: center;
  padding: 32px 0;
}
.ss-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}
.ss-card,
.ss-block {
  background: #f7f9fc;
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 12px 14px;
}
.ss-block {
  margin-top: 12px;
}
.ss-card-label {
  display: flex;
  align-items: center;
  gap: 6px;
  color: #5b6b7b;
  font-size: 12.5px;
  margin-bottom: 6px;
}
.ss-value {
  font-size: 20px;
  font-weight: 700;
  color: #1f2d3d;
  line-height: 1.3;
  margin-bottom: 6px;
  word-break: break-all;
}
.ss-sub {
  color: #8a97a5;
  font-size: 12.5px;
  margin-top: 6px;
  line-height: 1.45;
}
.ss-use :deep(.el-progress__text) {
  font-size: 12px !important;
  min-width: 36px;
}
.net-cell {
  display: flex;
  align-items: baseline;
  gap: 8px;
}
.net-rate {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-weight: 600;
  color: #2d6cdf;
}
.net-total {
  color: #a0abba;
  font-size: 11.5px;
}
@media (max-width: 640px) {
  .ss-grid {
    grid-template-columns: 1fr;
  }
  /* 窄屏：迷你条允许横向滚动 */
  .ss-bar {
    overflow-x: auto;
    gap: 4px;
  }
}
</style>