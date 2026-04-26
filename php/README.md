# SMMonitor PHP 管理端 + 控制台页面

这个包用于配合 .NET8 Windows 服务客户端 Agent 使用。

## 文件说明

```text
config.php          服务端配置
ws_server.php       Swoole WebSocket + HTTP 管理端
index.html          Element Plus 控制台页面
scripts/start.sh    后台启动脚本
scripts/stop.sh     停止脚本
scripts/status.sh   查看进程
runtime/            日志、截图临时文件目录
```

## 依赖

服务器需要安装：

```bash
php -m | grep swoole
php -m | grep redis
```

如果没有：

```bash
pecl install swoole
pecl install redis
```

并在 `php.ini` 中启用：

```ini
extension=swoole
extension=redis
```

需要 Redis 服务可用。

## 配置

编辑 `config.php`：

```php
'port' => 9502,
'client_token' => 'your-token',
'redis' => [
    'host' => '127.0.0.1',
    'port' => 6379,
    'auth' => '',
],
```

`client_token` 必须和 .NET Agent 配置里的 Token 一致。

## 启动

```bash
cd /www/wwwroot/your_console/SMMonitorPHPConsole
php ws_server.php
```

后台启动：

```bash
chmod +x scripts/*.sh
./scripts/start.sh
```

停止：

```bash
./scripts/stop.sh
```

查看：

```bash
./scripts/status.sh
```

## 控制台页面

把 `index.html` 放到你当前控制台目录即可。

它保留了你现在的引用路径：

```html
../libs/node_modules/element-plus/dist/index.css
../libs/node_modules/vue/dist/vue.global.prod.js
../libs/node_modules/element-plus/dist/index.full.min.js
../libs/node_modules/@element-plus/icons-vue/dist/index.iife.min.js
```

默认接口地址是：

```js
`${location.protocol}//${location.hostname}:9502`
```

如果你要固定接口地址，可以在 `index.html` 里改：

```js
apiBase: 'http://117.21.200.221:9502'
```

或者在页面前面加：

```html
<script>
window.SM_MONITOR_API_BASE = 'http://117.21.200.221:9502'
</script>
```

## HTTP 接口

### 获取在线客户端

```http
GET /online
```

返回：

```json
{
  "success": true,
  "clients": []
}
```

### 下发命令

```http
POST /send
Content-Type: application/json

{
  "clientId": "192.168.1.10",
  "action": "get_config",
  "payload": {}
}
```

返回：

```json
{
  "success": true,
  "requestId": "req_..."
}
```

### 轮询命令结果

```http
GET /result?requestId=req_xxx
```

### 访问截图临时文件

```http
GET /file?token=xxx
```

## WebSocket 客户端协议

### 资源上报

```json
{
  "type": "monitor",
  "clientId": "192.168.1.10",
  "token": "your-token",
  "payload": {
    "machineName": "WIN-PC",
    "version": "1.0.0",
    "cpu": 32.5,
    "memoryUsedPercent": 76.3,
    "memoryTotalMb": 16384,
    "memoryAvailableMb": 3880,
    "disks": [
      { "name": "C:\\", "totalGb": 237.5, "freeGb": 50.2, "usedPercent": 78.8 }
    ]
  }
}
```

### 服务端下发

服务端通过 WS push：

```json
{
  "type": "request",
  "requestId": "req_xxx",
  "action": "get_config",
  "payload": {}
}
```

### 客户端响应

```json
{
  "type": "response",
  "requestId": "req_xxx",
  "clientId": "192.168.1.10",
  "ok": true,
  "data": {
    "message": "ok"
  }
}
```

## 和你当前 HTML 的兼容说明

这个版本继续使用：

```text
/online
/send
/result
/file
```

并保留这些动作：

```text
get_config
set_config
show_message
command
reboot
```

其中：

- 原来的“机器重启”仍发送 `action=command`，payload 里是 `{ command: "machine_restart" }`
- “重启系统”发送 `action=command`，payload 为 `{ command: "machine_restart" }`，可直接执行系统重启
