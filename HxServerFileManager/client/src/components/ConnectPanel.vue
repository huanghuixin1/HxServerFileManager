<script setup>
import { ref, reactive, computed, watch } from 'vue'
import { api } from '../api.js'
import { useSettings } from '../useSettings.js'

// mode='connect'：连接并保存；mode='edit'：更新已保存的连接（initial 传入该连接）
const props = defineProps({
  initial: { type: Object, default: null },
  mode: { type: String, default: 'connect' },
})
const emit = defineEmits(['connected', 'updated', 'saved'])

const alias = ref('')
const host = ref('')
const port = ref(22)
const username = ref('')
const authType = ref('password')
const password = ref('')
const keyText = ref('')
const passphrase = ref('')
const error = ref('')
const busy = ref(false)

// ---- 代理：direct=直连（默认）follow=跟随全局 custom=单独配置 ----
// 「跟随全局」单选上要展示全局代理当前配置，挂载时拉一次偏好
const { proxy: globalProxy, ensureLoaded } = useSettings()
ensureLoaded()
const globalProxyDesc = computed(() => {
  const p = globalProxy.value
  if (!p || !p.host) return '（未配置，将直连）'
  const t = (p.type || 'http').toUpperCase()
  return `（${t} ${p.host}:${p.port || ''}）`
})
const proxyMode = ref('direct')
const proxy = reactive({ type: 'http', host: '', port: null, username: '', password: '' })

// 编辑模式：用已保存的连接预填表单（密码/私钥不返回，留空表示不修改）
watch(
  () => props.initial,
  (v) => {
    if (!v) return
    alias.value = v.name || ''
    host.value = v.host || ''
    port.value = v.port || 22
    username.value = v.username || ''
    authType.value = v.authType === 'key' ? 'key' : 'password'
    password.value = ''
    keyText.value = ''
    passphrase.value = ''
    proxyMode.value = v.proxyMode === 'follow' || v.proxyMode === 'custom' ? v.proxyMode : 'direct'
    proxy.type = v.proxy?.type || 'http'
    proxy.host = v.proxy?.host || ''
    proxy.port = v.proxy?.port || null
    proxy.username = v.proxy?.username || ''
    proxy.password = v.proxy?.password || ''
  },
  { immediate: true }
)

function buildReq() {
  return {
    name: alias.value.trim() || undefined,
    host: host.value.trim(),
    port: Number(port.value) || 22,
    username: username.value.trim(),
    password: authType.value === 'password' ? password.value : '',
    privateKey: authType.value === 'key' ? keyText.value : '',
    passphrase: authType.value === 'key' ? passphrase.value : '',
    proxyMode: proxyMode.value,
    proxy: proxyMode.value === 'custom'
      ? {
          type: proxy.type,
          host: proxy.host.trim(),
          port: Number(proxy.port) || null,
          username: proxy.username.trim() || null,
          password: proxy.password || null,
        }
      : null,
  }
}

// 仅保存：不发起连接，直接存为已保存连接（服务器连不上/凭据未定时先存起来）。
// 对已存在的同主机连接，密码/私钥留空则保留原值（后端合并）
async function saveOnly() {
  error.value = ''
  if (!host.value || !username.value) {
    error.value = 'Host 与 Username 为必填'
    return
  }
  busy.value = true
  try {
    const res = await api.saveConnection(buildReq())
    emit('saved', { id: res.id, name: res.name || alias.value })
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = false
  }
}

async function submit() {
  error.value = ''
  if (!host.value || !username.value) {
    error.value = 'Host 与 Username 为必填'
    return
  }
  busy.value = true
  try {
    if (props.mode === 'edit' && props.initial) {
      const res = await api.updateConnection(props.initial.id, buildReq())
      emit('updated', { id: res.id, name: res.name || alias.value })
    } else {
      const req = buildReq()
      const res = await api.connect(req)
      emit('connected', {
        connectionId: res.connectionId,
        host: res.host,
        username: res.username,
        port: req.port,
        authType: authType.value,
        name: res.name || alias.value,
        homeDirectory: res.homeDirectory || '/',
      })
    }
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="card connect">
    <h3 class="title">{{ mode === 'edit' ? '编辑已保存的连接' : '连接 Linux 服务器' }}</h3>
    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item label="别名 (可选)">
        <el-input
          v-model.trim="alias"
          placeholder="给这台服务器起个名字，例如 测试机"
          clearable
        />
      </el-form-item>

      <el-form-item label="主机 Host" required>
        <el-input
          v-model.trim="host"
          placeholder="例如 192.168.1.10 或 localhost"
          clearable
        />
      </el-form-item>

      <el-row :gutter="10">
        <el-col :xs="24" :sm="10">
          <el-form-item label="端口 Port">
            <el-input-number
              v-model="port"
              :min="1"
              :max="65535"
              controls-position="right"
              style="width: 100%"
            />
          </el-form-item>
        </el-col>
        <el-col :xs="24" :sm="14">
          <el-form-item label="用户名 Username" required>
            <el-input v-model.trim="username" placeholder="root" clearable />
          </el-form-item>
        </el-col>
      </el-row>

      <el-form-item label="认证方式">
        <el-radio-group v-model="authType">
          <el-radio value="password">密码</el-radio>
          <el-radio value="key">私钥</el-radio>
        </el-radio-group>
      </el-form-item>

      <template v-if="authType === 'password'">
        <el-form-item label="密码 Password">
          <el-input
            v-model="password"
            type="password"
            :placeholder="mode === 'edit' ? '留空表示不修改原密码' : '••••••'"
            show-password
          />
        </el-form-item>
      </template>
      <template v-else>
        <el-form-item label="私钥 (PEM / OPENSSH)">
          <el-input
            v-model="keyText"
            type="textarea"
            :rows="4"
            :placeholder="mode === 'edit' ? '留空表示不修改原私钥' : '-----BEGIN OPENSSH PRIVATE KEY-----'"
            class="key-textarea"
          />
        </el-form-item>
        <el-form-item label="私钥口令 (可选)">
          <el-input
            v-model="passphrase"
            type="password"
            :placeholder="mode === 'edit' ? '留空表示不修改' : ''"
            show-password
          />
        </el-form-item>
      </template>

      <el-form-item label="代理">
        <el-radio-group v-model="proxyMode">
          <el-radio value="direct">直连</el-radio>
          <el-radio value="follow">跟随全局{{ globalProxyDesc }}</el-radio>
          <el-radio value="custom">自定义</el-radio>
        </el-radio-group>
      </el-form-item>

      <template v-if="proxyMode === 'custom'">
        <el-row :gutter="10">
          <el-col :xs="24" :sm="10">
            <el-form-item label="代理类型">
              <el-select v-model="proxy.type">
                <el-option label="HTTP" value="http" />
                <el-option label="SOCKS5" value="socks5" />
                <el-option label="SOCKS4" value="socks4" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="14">
            <el-form-item label="代理主机" required>
              <el-input v-model.trim="proxy.host" placeholder="例如 127.0.0.1" clearable />
            </el-form-item>
          </el-col>
        </el-row>
        <el-row :gutter="10">
          <el-col :xs="24" :sm="10">
            <el-form-item label="代理端口" required>
              <el-input-number
                v-model="proxy.port"
                :min="1"
                :max="65535"
                controls-position="right"
                style="width: 100%"
              />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="14">
            <el-form-item label="代理用户名">
              <el-input v-model.trim="proxy.username" placeholder="可选" clearable />
            </el-form-item>
          </el-col>
        </el-row>
        <el-form-item label="代理密码">
          <el-input v-model="proxy.password" type="password" placeholder="可选" show-password />
        </el-form-item>
      </template>

      <el-alert
        v-if="error"
        :title="error"
        type="error"
        :closable="false"
        show-icon
      />

      <div v-if="mode === 'connect'" class="btn-row">
        <el-button type="primary" native-type="submit" :loading="busy" class="btn-grow">
          {{ busy ? '连接中…' : '连接并保存' }}
        </el-button>
        <el-button
          class="btn-grow"
          :disabled="busy"
          title="不发起连接，直接保存；已存在的同主机连接会更新，密码/私钥留空则保留原值"
          @click="saveOnly"
        >
          仅保存
        </el-button>
      </div>
      <el-button
        v-else
        type="primary"
        native-type="submit"
        :loading="busy"
        style="width: 100%"
      >
        {{ busy ? '保存中…' : '保存修改' }}
      </el-button>
    </el-form>
  </div>
</template>

<style scoped>
.title {
  margin: 0 0 14px;
  font-size: 15px;
  color: #1f2d3d;
}
.el-form-item {
  margin-bottom: 16px;
}
.key-textarea :deep(textarea) {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 12.5px;
}
.btn-row {
  display: flex;
  gap: 10px;
}
.btn-grow {
  flex: 1;
}
</style>
