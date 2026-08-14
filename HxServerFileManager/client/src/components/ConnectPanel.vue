<script setup>
import { ref } from 'vue'
import { api } from '../api.js'

const emit = defineEmits(['connected'])

const host = ref('')
const port = ref(22)
const username = ref('')
const authType = ref('password')
const password = ref('')
const keyText = ref('')
const passphrase = ref('')
const error = ref('')
const busy = ref(false)

async function submit() {
  error.value = ''
  if (!host.value || !username.value) {
    error.value = 'Host 与 Username 为必填'
    return
  }
  busy.value = true
  try {
    const req = {
      host: host.value,
      port: Number(port.value) || 22,
      username: username.value,
      password: authType.value === 'password' ? password.value : '',
      privateKey: authType.value === 'key' ? keyText.value : '',
      passphrase: authType.value === 'key' ? passphrase.value : '',
    }
    const res = await api.connect(req)
    emit('connected', {
      connectionId: res.connectionId,
      host: res.host,
      username: res.username,
      port: req.port,
      authType: authType.value,
    })
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="card connect">
    <h3 class="title">连接 Linux 服务器</h3>
    <el-form label-position="top" @submit.prevent="submit">
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
            placeholder="••••••"
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
            placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
            class="key-textarea"
          />
        </el-form-item>
        <el-form-item label="私钥口令 (可选)">
          <el-input v-model="passphrase" type="password" show-password />
        </el-form-item>
      </template>

      <el-alert
        v-if="error"
        :title="error"
        type="error"
        :closable="false"
        show-icon
      />

      <el-button
        type="primary"
        native-type="submit"
        :loading="busy"
        style="width: 100%"
      >
        {{ busy ? '连接中…' : '连接' }}
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
</style>
