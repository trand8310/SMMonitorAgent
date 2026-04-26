<?php
/**
 * SMMonitor PHP WebSocket 管理端配置
 */
return [
    // WebSocket + HTTP 服务监听
    'host' => '0.0.0.0',
    'port' => 9502,

    // 客户端 Agent 上报 token，需要和 .NET 客户端配置一致
    'client_token' => 'ce83aaac',

    // 管理端 token，留空表示 HTML 控制台不校验管理 token。
    // 如果设置了值，前端请求需要带 Header: X-Admin-Token
    'admin_token' => '',

    // Redis 配置
    'redis' => [
        'host' => '127.0.0.1',
        'port' => 6379,
        'auth' => 'p86JEZzrl2ebn6Y0',
        'db'   => 0,
        'timeout' => 2.0,
    ],

    // Redis Key 前缀
    'key_prefix' => 'smmonitor:',

    // 多久没有心跳认为离线
    'offline_seconds' => 30,

    // /result 轮询超时时间
    'request_timeout_seconds' => 60,
    // 截图类请求超时时间（screen_screenshot / app_screenshot）
    'screenshot_request_timeout_seconds' => 200,

    // 请求结果在 Redis 保存多久
    'request_ttl_seconds' => 300,

    // 客户端状态保存多久
    'client_ttl_seconds' => 180,

    // 截图保存目录
    'screenshot_dir' => __DIR__ . '/runtime/screenshots',

    // /file?token=xxx 文件访问 token 保存时间
    'file_token_ttl_seconds' => 600,

    // 跨域，生产环境建议改成你的控制台域名
    'cors_allow_origin' => '*',
];
