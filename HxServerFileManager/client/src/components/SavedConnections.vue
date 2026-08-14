<script setup>
import { ref, onMounted } from 'vue'
import { api } from '../api.js'

const emit = defineEmits(['reconnect'])

const items = ref([])
const error = ref('')
const busyId = ref(null)

async function load() {
  try {
    const res = await api.listConnections()
    items.value = res.connections || []
  } catch (e) {
    error.value = e.message
  }
}
onMounted(load)

async function doReconnect(item) {
  error.value = ''
  busyId.value = item.id
  try {
    const res = await api.reconnect(item.id)
    emit('reconnect', {
      connectionId: res.connectionId,
      host: res.host,
      username: res.username,
      port: item.port,
      authType: item.authType,
    })
    await load() // 刷新最近连接排序
  } catch (e) {
    error.value = e.message
  } finally {
    busyId.value = null
  }
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
    <h3 class="title">已保存的连接</h3>

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
          <div class="name">{{ it.name }}</div>
          <div class="sub">
            {{ it.username }} · {{ it.host }}:{{ it.port }}
            <el-tag
              size="small"
              :type="it.authType === 'key' ? 'warning' : 'info'"
              effect="plain"
              style="margin-left: 6px"
            >
              {{ it.authType === 'key' ? '私钥' : '密码' }}
            </el-tag>
          </div>
        </div>
        <div class="btns">
          <el-button
            type="primary"
            size="small"
            :loading="busyId === it.id"
            @click="doReconnect(it)"
          >
            {{ busyId === it.id ? '重连中…' : '重连' }}
          </el-button>
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
  margin: 0 0 12px;
  font-size: 15px;
  color: #1f2d3d;
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
}
.sub {
  font-size: 12px;
  color: #7a8794;
}
.btns {
  display: flex;
  gap: 6px;
  flex-shrink: 0;
}
</style>
