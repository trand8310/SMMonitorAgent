<?php
declare(strict_types=1);

/**
 * SMMonitor PHP WebSocket 管理端
 *
 * 需要扩展：
 *   php --ri swoole
 *   php --ri redis
 *
 * 启动：
 *   php ws_server.php
 */

date_default_timezone_set('Asia/Shanghai');

if (!extension_loaded('swoole')) {
    fwrite(STDERR, "ERROR: swoole extension is not loaded.\n");
    exit(1);
}
if (!extension_loaded('redis')) {
    fwrite(STDERR, "ERROR: redis extension is not loaded.\n");
    exit(1);
}

use Swoole\WebSocket\Server;
use Swoole\Http\Request;
use Swoole\Http\Response;

$config = require __DIR__ . '/config.php';

$host = (string)$config['host'];
$port = (int)$config['port'];
$prefix = (string)$config['key_prefix'];

/**
 * 内存映射，worker_num 建议保持 1，避免 fd 映射跨进程问题。
 */
$clientFdMap = []; // clientId => fd
$fdClientMap = []; // fd => clientId

function redis_conn(array $config): Redis
{
    $r = new Redis();
    $rc = $config['redis'];

    $r->connect(
        (string)$rc['host'],
        (int)$rc['port'],
        (float)($rc['timeout'] ?? 2.0)
    );

    if (!empty($rc['auth'])) {
        $r->auth((string)$rc['auth']);
    }

    if (isset($rc['db'])) {
        $r->select((int)$rc['db']);
    }

    return $r;
}

function now_ts(): int
{
    return time();
}

function json_encode_cn($data): string
{
    return json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
}

function send_json(Response $response, array $data, int $status = 200): void
{
    $response->status($status);
    $response->header('Content-Type', 'application/json; charset=utf-8');
    $response->end(json_encode_cn($data));
}

function apply_cors(Response $response, array $config): void
{
    $origin = (string)($config['cors_allow_origin'] ?? '*');
    $response->header('Access-Control-Allow-Origin', $origin);
    $response->header('Access-Control-Allow-Methods', 'GET,POST,OPTIONS');
    $response->header('Access-Control-Allow-Headers', 'Content-Type, X-Admin-Token');
}

function is_admin_allowed(Request $request, array $config): bool
{
    $adminToken = (string)($config['admin_token'] ?? '');
    if ($adminToken === '') {
        return true;
    }

    $headers = $request->header ?? [];
    $token = $headers['x-admin-token'] ?? $headers['X-Admin-Token'] ?? '';
    if ($token === $adminToken) {
        return true;
    }

    $queryToken = $request->get['adminToken'] ?? '';
    return $queryToken === $adminToken;
}

function request_json_body(Request $request): array
{
    $raw = $request->rawContent();
    if (!is_string($raw) || trim($raw) === '') {
        return [];
    }

    $data = json_decode($raw, true);
    return is_array($data) ? $data : [];
}

function normalize_payload_client(array $data): array
{
    $payload = isset($data['payload']) && is_array($data['payload']) ? $data['payload'] : [];

    return [
        'clientId'       => (string)($data['clientId'] ?? $payload['clientId'] ?? ''),
        'machineName'    => (string)($data['machineName'] ?? $payload['machineName'] ?? ''),
        'version'        => (string)($data['version'] ?? $payload['version'] ?? ''),
        'group'          => (string)($data['group'] ?? $payload['group'] ?? ''),
        'localIp'        => (string)($data['localIp'] ?? $payload['localIp'] ?? ''),
        'payload'        => $payload,
        'lastHeartbeat'  => now_ts(),
    ];
}

function save_client_state(Redis $redis, array $config, string $prefix, string $clientId, int $fd, array $info): void
{
    $key = $prefix . 'client:' . $clientId;
    $allKey = $prefix . 'clients';

    $old = [];
    $oldJson = $redis->get($key);
    if (is_string($oldJson) && $oldJson !== '') {
        $decoded = json_decode($oldJson, true);
        if (is_array($decoded)) {
            $old = $decoded;
        }
    }

    $payload = $info['payload'] ?? [];

    $row = array_merge($old, [
        'clientId'      => $clientId,
        'fd'            => $fd,
        'machineName'   => $info['machineName'] ?: ($old['machineName'] ?? ($payload['machineName'] ?? '')),
        'version'       => $info['version'] ?: ($old['version'] ?? ($payload['version'] ?? '')),
        'group'         => $info['group'] ?: ($old['group'] ?? ($payload['group'] ?? '')),
        'localIp'       => $info['localIp'] ?: ($old['localIp'] ?? ''),
        'payload'       => $payload ?: ($old['payload'] ?? []),
        'lastHeartbeat' => now_ts(),
    ]);

    if (empty($row['connectedAt'])) {
        $row['connectedAt'] = now_ts();
    }

    // 常用监控字段扁平化，方便 HTML 表格直接使用
    $row['cpu'] = $payload['cpu'] ?? $payload['Cpu'] ?? ($old['cpu'] ?? null);
    $row['memoryUsedPercent'] = $payload['memoryUsedPercent'] ?? $payload['MemoryUsedPercent'] ?? ($old['memoryUsedPercent'] ?? null);
    $row['memoryTotalMb'] = $payload['memoryTotalMb'] ?? $payload['MemoryTotalMb'] ?? ($old['memoryTotalMb'] ?? null);
    $row['memoryAvailableMb'] = $payload['memoryAvailableMb'] ?? $payload['MemoryAvailableMb'] ?? ($old['memoryAvailableMb'] ?? null);
    $row['disks'] = $payload['disks'] ?? $payload['Disks'] ?? ($old['disks'] ?? []);

    $ttl = (int)($config['client_ttl_seconds'] ?? 180);

    $redis->setex($key, $ttl, json_encode_cn($row));
    $redis->sAdd($allKey, $clientId);
}

function make_request_id(): string
{
    return 'req_' . date('YmdHis') . '_' . bin2hex(random_bytes(4));
}

function save_pending_request(Redis $redis, array $config, string $prefix, string $requestId, array $row): void
{
    $ttl = (int)($config['request_ttl_seconds'] ?? 300);
    $redis->setex($prefix . 'request:' . $requestId, $ttl, json_encode_cn($row));
}

function get_request_row(Redis $redis, string $prefix, string $requestId): ?array
{
    $json = $redis->get($prefix . 'request:' . $requestId);
    if (!is_string($json) || $json === '') {
        return null;
    }
    $data = json_decode($json, true);
    return is_array($data) ? $data : null;
}

function save_base64_file_if_needed(Redis $redis, array $config, string $prefix, array $responseData): array
{
    // 支持两种常见结构：
    // 1) response.data.imageBase64
    // 2) data.imageBase64
    $candidates = [];

    if (isset($responseData['data']) && is_array($responseData['data'])) {
        $candidates[] = ['path' => ['data'], 'data' => $responseData['data']];
    }

    if (isset($responseData['response']) && is_array($responseData['response'])) {
        if (isset($responseData['response']['data']) && is_array($responseData['response']['data'])) {
            $candidates[] = ['path' => ['response', 'data'], 'data' => $responseData['response']['data']];
        }
    }

    foreach ($candidates as $candidate) {
        $data = $candidate['data'];
        $imageBase64 = (string)($data['imageBase64'] ?? '');
        if ($imageBase64 === '') {
            continue;
        }

        $contentType = (string)($data['contentType'] ?? 'image/jpeg');
        $ext = match ($contentType) {
            'image/png' => 'png',
            'image/webp' => 'webp',
            default => 'jpg',
        };

        $dir = (string)$config['screenshot_dir'] . '/' . date('Ymd');
        if (!is_dir($dir)) {
            mkdir($dir, 0775, true);
        }

        $token = bin2hex(random_bytes(16));
        $file = $dir . '/' . $token . '.' . $ext;

        $binary = base64_decode($imageBase64, true);
        if ($binary === false) {
            continue;
        }

        file_put_contents($file, $binary);

        $fileMap = [
            'file' => $file,
            'contentType' => $contentType,
            'createdAt' => now_ts(),
        ];

        $redis->setex(
            $prefix . 'file:' . $token,
            (int)($config['file_token_ttl_seconds'] ?? 600),
            json_encode_cn($fileMap)
        );

        // 移除大 base64，改为临时文件 URL，避免 Redis 和 HTTP 响应过大
        $fileUrl = '/file?token=' . $token;

        if ($candidate['path'] === ['data']) {
            unset($responseData['data']['imageBase64']);
            $responseData['data']['fileUrl'] = $fileUrl;
            $responseData['data']['contentType'] = $contentType;
        } elseif ($candidate['path'] === ['response', 'data']) {
            unset($responseData['response']['data']['imageBase64']);
            $responseData['response']['data']['fileUrl'] = $fileUrl;
            $responseData['response']['data']['contentType'] = $contentType;
        }
    }

    return $responseData;
}

$server = new Server($host, $port, SWOOLE_PROCESS, SWOOLE_SOCK_TCP);

$server->set([
    'worker_num' => 1,
    'max_request' => 0,
    'heartbeat_idle_time' => 60,
    'heartbeat_check_interval' => 10,
    'log_file' => __DIR__ . '/runtime/swoole.log',
]);

$server->on('Start', function (Server $server) use ($host, $port): void {
    echo "SMMonitor WS/HTTP server started at {$host}:{$port}\n";
});

$server->on('Open', function (Server $server, Request $request): void {
    echo '[' . date('Y-m-d H:i:s') . "] open fd={$request->fd}\n";
});

$server->on('Message', function (Server $server, Swoole\WebSocket\Frame $frame) use (&$clientFdMap, &$fdClientMap, $config, $prefix): void {
    $data = json_decode($frame->data, true);
    if (!is_array($data)) {
        return;
    }

    $type = (string)($data['type'] ?? '');
    $clientToken = (string)($config['client_token'] ?? '');

    try {
        $redis = redis_conn($config);
    } catch (Throwable $e) {
        echo "Redis error: {$e->getMessage()}\n";
        return;
    }

    // register / heartbeat / monitor 都需要 token
    if (in_array($type, ['register', 'heartbeat', 'monitor'], true)) {
        if (($data['token'] ?? '') !== $clientToken) {
            echo "invalid token fd={$frame->fd}\n";
            $server->disconnect($frame->fd);
            return;
        }

        $info = normalize_payload_client($data);
        $clientId = $info['clientId'];

        if ($clientId === '') {
            return;
        }

        $clientFdMap[$clientId] = $frame->fd;
        $fdClientMap[$frame->fd] = $clientId;

        save_client_state($redis, $config, $prefix, $clientId, $frame->fd, $info);

        if ($type === 'monitor') {
            $cpu = $info['payload']['cpu'] ?? $info['payload']['Cpu'] ?? '-';
            $mem = $info['payload']['memoryUsedPercent'] ?? $info['payload']['MemoryUsedPercent'] ?? '-';
            echo '[' . date('H:i:s') . "] monitor {$clientId} cpu={$cpu} mem={$mem}\n";
        }

        return;
    }

    if ($type === 'response') {
        $clientId = (string)($data['clientId'] ?? ($fdClientMap[$frame->fd] ?? ''));
        $requestId = (string)($data['requestId'] ?? '');

        if ($clientId !== '') {
            $clientFdMap[$clientId] = $frame->fd;
            $fdClientMap[$frame->fd] = $clientId;
        }

        if ($requestId === '') {
            return;
        }

        $responseData = save_base64_file_if_needed($redis, $config, $prefix, $data);

        $old = get_request_row($redis, $prefix, $requestId) ?? [];

        $row = array_merge($old, [
            'success' => true,
            'status' => 'done',
            'requestId' => $requestId,
            'clientId' => $clientId,
            'response' => $responseData,
            'updatedAt' => now_ts(),
        ]);

        save_pending_request($redis, $config, $prefix, $requestId, $row);

        echo '[' . date('H:i:s') . "] response {$clientId} requestId={$requestId}\n";
    }
});

$server->on('Close', function (Server $server, int $fd) use (&$clientFdMap, &$fdClientMap): void {
    if (isset($fdClientMap[$fd])) {
        $clientId = $fdClientMap[$fd];
        unset($fdClientMap[$fd], $clientFdMap[$clientId]);
        echo '[' . date('Y-m-d H:i:s') . "] close client={$clientId} fd={$fd}\n";
    } else {
        echo '[' . date('Y-m-d H:i:s') . "] close fd={$fd}\n";
    }
});

$server->on('Request', function (Request $request, Response $response) use ($server, &$clientFdMap, $config, $prefix): void {
    apply_cors($response, $config);

    $method = strtoupper((string)($request->server['request_method'] ?? 'GET'));
    $path = (string)($request->server['request_uri'] ?? '/');

    if ($method === 'OPTIONS') {
        $response->status(204);
        $response->end();
        return;
    }

    if ($path !== '/health' && $path !== '/file' && !is_admin_allowed($request, $config)) {
        send_json($response, ['success' => false, 'message' => 'admin token invalid'], 401);
        return;
    }

    try {
        $redis = redis_conn($config);
    } catch (Throwable $e) {
        send_json($response, ['success' => false, 'message' => 'Redis连接失败: ' . $e->getMessage()], 500);
        return;
    }

    if ($path === '/' || $path === '/health') {
        send_json($response, [
            'success' => true,
            'message' => 'SMMonitor server ok',
            'time' => now_ts(),
        ]);
        return;
    }

    if ($path === '/online' || $path === '/api/clients') {
        $ids = $redis->sMembers($prefix . 'clients') ?: [];
        $rows = [];
        $now = now_ts();
        $offlineSeconds = (int)($config['offline_seconds'] ?? 30);

        foreach ($ids as $clientId) {
            $clientId = (string)$clientId;
            $json = $redis->get($prefix . 'client:' . $clientId);
            if (!is_string($json) || $json === '') {
                continue;
            }

            $row = json_decode($json, true);
            if (!is_array($row)) {
                continue;
            }

            $fd = $clientFdMap[$clientId] ?? null;
            $recent = ($now - (int)($row['lastHeartbeat'] ?? 0)) <= $offlineSeconds;
            $online = $fd !== null && $server->isEstablished((int)$fd) && $recent;

            $row['online'] = $online;
            $row['fd'] = $fd ?: ($row['fd'] ?? 0);

            $rows[] = $row;
        }

        usort($rows, function ($a, $b) {
            $ao = !empty($a['online']) ? 1 : 0;
            $bo = !empty($b['online']) ? 1 : 0;
            if ($ao !== $bo) {
                return $bo <=> $ao;
            }
            return (int)($b['lastHeartbeat'] ?? 0) <=> (int)($a['lastHeartbeat'] ?? 0);
        });

        send_json($response, [
            'success' => true,
            'clients' => $rows,
            'data' => $rows,
            'time' => $now,
        ]);
        return;
    }

    if ($path === '/send') {
        if ($method !== 'POST') {
            send_json($response, ['success' => false, 'message' => 'method not allowed'], 405);
            return;
        }

        $body = request_json_body($request);
        $clientId = trim((string)($body['clientId'] ?? ''));
        $action = trim((string)($body['action'] ?? ''));
        $payload = $body['payload'] ?? [];

        if ($clientId === '' || $action === '') {
            send_json($response, ['success' => false, 'message' => 'clientId/action required'], 400);
            return;
        }

        $fd = $clientFdMap[$clientId] ?? null;
        if ($fd === null || !$server->isEstablished((int)$fd)) {
            send_json($response, ['success' => false, 'message' => 'client offline or websocket not established'], 200);
            return;
        }

        $requestId = make_request_id();

        $message = [
            'type' => 'request',
            'requestId' => $requestId,
            'action' => $action,
            'payload' => is_array($payload) ? $payload : [],
            'ts' => now_ts(),
        ];

        $row = [
            'success' => true,
            'status' => 'pending',
            'requestId' => $requestId,
            'clientId' => $clientId,
            'action' => $action,
            'payload' => $payload,
            'createdAt' => now_ts(),
            'updatedAt' => now_ts(),
        ];

        save_pending_request($redis, $config, $prefix, $requestId, $row);

        $ok = $server->push((int)$fd, json_encode_cn($message));

        if (!$ok) {
            $row['status'] = 'failed';
            $row['success'] = false;
            $row['message'] = 'push failed';
            save_pending_request($redis, $config, $prefix, $requestId, $row);

            send_json($response, [
                'success' => false,
                'message' => 'push failed',
                'requestId' => $requestId,
            ]);
            return;
        }

        send_json($response, [
            'success' => true,
            'message' => 'sent',
            'requestId' => $requestId,
        ]);
        return;
    }

    if ($path === '/result') {
        $requestId = trim((string)($request->get['requestId'] ?? ''));
        if ($requestId === '') {
            send_json($response, ['success' => false, 'message' => 'requestId required'], 400);
            return;
        }

        $row = get_request_row($redis, $prefix, $requestId);
        if (!$row) {
            send_json($response, [
                'success' => false,
                'status' => 'not_found',
                'message' => 'request not found',
            ]);
            return;
        }

        if (($row['status'] ?? '') === 'pending') {
            $createdAt = (int)($row['createdAt'] ?? now_ts());
            $timeout = (int)($config['request_timeout_seconds'] ?? 60);

            if (now_ts() - $createdAt > $timeout) {
                $row['status'] = 'timeout';
                $row['success'] = true;
                $row['message'] = 'request timeout';
                $row['updatedAt'] = now_ts();
                save_pending_request($redis, $config, $prefix, $requestId, $row);
            }
        }

        send_json($response, $row);
        return;
    }

    if ($path === '/file') {
        $token = trim((string)($request->get['token'] ?? ''));
        if ($token === '') {
            $response->status(404);
            $response->end('not found');
            return;
        }

        $json = $redis->get($prefix . 'file:' . $token);
        if (!is_string($json) || $json === '') {
            $response->status(404);
            $response->end('not found');
            return;
        }

        $info = json_decode($json, true);
        if (!is_array($info) || empty($info['file']) || !is_file($info['file'])) {
            $response->status(404);
            $response->end('not found');
            return;
        }

        $response->header('Content-Type', (string)($info['contentType'] ?? 'application/octet-stream'));
        $response->header('Cache-Control', 'private, max-age=300');
        $response->sendfile((string)$info['file']);
        return;
    }

    send_json($response, ['success' => false, 'message' => 'not found'], 404);
});

$server->start();
