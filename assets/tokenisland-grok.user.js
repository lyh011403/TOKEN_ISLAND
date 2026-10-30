// ==UserScript==
// @name         TokenIsland - Grok 用量同步助手
// @namespace    https://tokenisland.local/
// @version      1.1.0
// @description  自動精準擷取 X.com (Twitter) Grok 官方機器人剩餘額度、已用訊息與冷卻重設時間，即時同步至 TokenIsland 靈動島
// @author       TokenIsland
// @match        https://x.com/*
// @match        https://twitter.com/*
// @grant        GM_xmlhttpRequest
// @grant        GM_registerMenuCommand
// @connect      127.0.0.1
// @run-at       document-start
// ==/UserScript==

(function() {
    'use strict';

    const SYNC_URL = 'http://127.0.0.1:19823/sync/grok';
    let lastSyncTime = 0;
    let lastRemaining = -1;

    function log(msg) {
        console.log('[TokenIsland Grok Sync] ' + msg);
    }

    // 發送同步封包至 TokenIsland 本地服務
    function sendSync(payload, isManual) {
        const now = Date.now();
        if (!isManual && now - lastSyncTime < 2500 && payload.remaining === lastRemaining) {
            return;
        }

        payload.timestamp = new Date().toISOString();
        const jsonStr = JSON.stringify(payload);

        const doRequest = (typeof GM_xmlhttpRequest !== 'undefined') ? GM_xmlhttpRequest : null;
        if (doRequest) {
            doRequest({
                method: 'POST',
                url: SYNC_URL,
                headers: { 'Content-Type': 'application/json' },
                data: jsonStr,
                onload: function(res) {
                    lastSyncTime = now;
                    lastRemaining = payload.remaining;
                    log('同步成功: 剩餘 ' + (payload.remaining != null ? payload.remaining.toFixed(1) + '%' : '未知') + ' (' + (payload.used_messages || 0) + '/' + (payload.total_messages || '?') + ' 則)');
                    if (isManual) alert('TokenIsland 同步成功！\n模型：' + payload.model + '\n剩餘：' + (payload.remaining != null ? payload.remaining.toFixed(1) + '%' : '已同步') + '\n次數：已用 ' + (payload.used_messages || 0) + ' / 上限 ' + (payload.total_messages || '?') + ' 則');
                },
                onerror: function(err) {
                    log('同步失敗 (請確認 TokenIsland 已在後台運行): ' + err);
                    if (isManual) alert('同步失敗！請確認 TokenIsland 已開啟並在後台運行。');
                }
            });
        } else {
            fetch(SYNC_URL, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: jsonStr
            }).then(() => {
                lastSyncTime = now;
                lastRemaining = payload.remaining;
                log('Fetch 同步成功');
                if (isManual) alert('TokenIsland 同步成功！');
            }).catch(e => {
                log('Fetch 同步失敗: ' + e);
                if (isManual) alert('同步失敗！請確認 TokenIsland 已開啟。');
            });
        }
    }

    // 嚴格判定是否為 Grok 專屬請求（杜絕 Twitter 首頁推文/通知 API 誤入）
    function isGrokUrl(url) {
        if (!url || typeof url !== 'string') return false;
        const u = url.toLowerCase();
        return u.includes('grok') ||
               u.includes('create_grok_conversation') ||
               u.includes('grok_conversation') ||
               u.includes('rate_limit_grok');
    }

    // 解析 rate limit 與 quota 資料
    function parseAndSyncGrok(url, headers, jsonBody) {
        if (!isGrokUrl(url)) return; // 嚴格過濾：非 Grok 專屬請求直接丟棄！

        try {
            let limit = null;
            let remaining = null;
            let reset = null;
            let model = 'Grok';

            // 1. 檢查 Grok 專屬 HTTP Header
            if (headers) {
                const hLimit = headers.get ? headers.get('x-rate-limit-limit') : headers['x-rate-limit-limit'];
                const hRemaining = headers.get ? headers.get('x-rate-limit-remaining') : headers['x-rate-limit-remaining'];
                const hReset = headers.get ? headers.get('x-rate-limit-reset') : headers['x-rate-limit-reset'];

                // 排除 Twitter 預設的 900 / 180 通用 Rate-Limit
                if (hLimit && parseInt(hLimit, 10) <= 100) {
                    limit = parseFloat(hLimit);
                    if (hRemaining) remaining = parseFloat(hRemaining);
                    if (hReset) reset = parseInt(hReset, 10);
                }
            }

            // 2. 深度搜尋 JSON 結構中的 Grok 專屬配額物件
            if (jsonBody && typeof jsonBody === 'object') {
                const searchObj = function(obj, depth) {
                    if (!obj || depth > 5) return;
                    if (obj.rate_limit || obj.rateLimit) {
                        const rl = obj.rate_limit || obj.rateLimit;
                        if (rl.limit != null) limit = parseFloat(rl.limit);
                        if (rl.remaining != null) remaining = parseFloat(rl.remaining);
                        if (rl.reset != null) reset = parseInt(rl.reset, 10);
                        if (rl.resets_at != null) reset = parseInt(rl.resets_at, 10);
                    }
                    if (obj.model && typeof obj.model === 'string') {
                        model = obj.model;
                    }
                    if (obj.remaining_queries != null) remaining = parseFloat(obj.remaining_queries);
                    if (obj.query_limit != null) limit = parseFloat(obj.query_limit);
                    if (obj.remaining_messages != null) remaining = parseFloat(obj.remaining_messages);
                    if (obj.total_messages != null) limit = parseFloat(obj.total_messages);

                    for (const k of Object.keys(obj)) {
                        if (typeof obj[k] === 'object' && obj[k] !== null) {
                            searchObj(obj[k], depth + 1);
                        }
                    }
                };
                searchObj(jsonBody, 0);
            }

            // 計算百分比與已用次數
            let percent = null;
            let used = null;
            if (remaining != null && limit != null && limit > 0) {
                percent = Math.max(0, Math.min(100, (remaining / limit) * 100));
                used = Math.max(0, limit - remaining);
            } else if (remaining != null && remaining <= 100) {
                percent = remaining;
            }

            if (percent != null || remaining != null || used != null) {
                sendSync({
                    remaining: percent != null ? percent : remaining,
                    used_messages: used,
                    total_messages: limit,
                    model: model,
                    window_minutes: 120,
                    resets_at: reset
                }, false);
            }
        } catch (e) {
            log('解析失敗: ' + e);
        }
    }

    // 攔截 window.fetch（僅攔截真正的 Grok 相關請求）
    const rawFetch = window.fetch;
    window.fetch = async function(...args) {
        const response = await rawFetch.apply(this, args);
        try {
            const url = (args[0] && args[0].url) ? args[0].url : String(args[0]);
            if (isGrokUrl(url)) {
                const clone = response.clone();
                clone.json().then(data => {
                    parseAndSyncGrok(url, response.headers, data);
                }).catch(() => {
                    parseAndSyncGrok(url, response.headers, null);
                });
            }
        } catch (e) {}
        return response;
    };

    // 攔截 XMLHttpRequest（僅攔截真正的 Grok 相關請求）
    const rawOpen = XMLHttpRequest.prototype.open;
    const rawSend = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.open = function(method, url, ...rest) {
        this._tokenIslandUrl = url;
        return rawOpen.apply(this, [method, url, ...rest]);
    };
    XMLHttpRequest.prototype.send = function(...args) {
        this.addEventListener('load', function() {
            try {
                const url = this._tokenIslandUrl || '';
                if (isGrokUrl(url)) {
                    let json = null;
                    try { json = JSON.parse(this.responseText); } catch (e) {}
                    const headers = {
                        'x-rate-limit-limit': this.getResponseHeader('x-rate-limit-limit'),
                        'x-rate-limit-remaining': this.getResponseHeader('x-rate-limit-remaining'),
                        'x-rate-limit-reset': this.getResponseHeader('x-rate-limit-reset')
                    };
                    parseAndSyncGrok(url, headers, json);
                }
            } catch (e) {}
        });
        return rawSend.apply(this, args);
    };

    // DOM 備援監控 (當使用者停留在 https://x.com/i/grok 頁面時)
    function scanPageText() {
        if (!location.pathname.includes('/grok')) return;
        try {
            // 優先在輸入框容器與底部欄位尋找配額字樣
            const textNodes = document.querySelectorAll('span, div, p');
            for (let i = 0; i < textNodes.length; i++) {
                const t = textNodes[i].textContent;
                if (!t || t.length > 80) continue;

                // 匹配如: "42 of 50 messages" / "42/50 則訊息" / "42 messages remaining" / "剩餘 42 則"
                const matchEn = t.match(/(\d+)\s*(?:of|\/)\s*(\d+)\s*messages?/i) || t.match(/(\d+)\s*messages?\s*remaining/i);
                const matchZh = t.match(/(\d+)\s*(?:\/|之)\s*(\d+)\s*則/) || t.match(/剩餘\s*(\d+)\s*則/);

                if (matchEn || matchZh) {
                    let rem = null, tot = 50;
                    if (matchEn && matchEn[2]) {
                        rem = parseInt(matchEn[1], 10);
                        tot = parseInt(matchEn[2], 10);
                    } else if (matchEn) {
                        rem = parseInt(matchEn[1], 10);
                    } else if (matchZh && matchZh[2]) {
                        rem = parseInt(matchZh[1], 10);
                        tot = parseInt(matchZh[2], 10);
                    } else if (matchZh) {
                        rem = parseInt(matchZh[1], 10);
                    }

                    if (rem != null && rem <= tot) {
                        sendSync({
                            remaining: Math.max(0, Math.min(100, (rem / tot) * 100)),
                            used_messages: Math.max(0, tot - rem),
                            total_messages: tot,
                            model: 'Grok',
                            window_minutes: 120,
                            resets_at: Math.floor(Date.now() / 1000) + 7200
                        }, false);
                        break;
                    }
                }
            }
        } catch (e) {}
    }

    setInterval(scanPageText, 6000);

    // 註冊 Tampermonkey 選單指令
    if (typeof GM_registerMenuCommand !== 'undefined') {
        GM_registerMenuCommand('✏️ 手動校準 Grok 剩餘次數 (如: 40/50 則)', function() {
            const input = prompt('請輸入目前的 Grok 配額：\n格式範例：40/50（剩餘 40 則，上限 50 則）\n或直接輸入剩餘百分比（例如：80%）', '40/50');
            if (!input) return;
            let rem = 40, tot = 50, percent = 80;
            if (input.includes('/')) {
                const parts = input.split('/');
                rem = parseInt(parts[0].trim(), 10) || 40;
                tot = parseInt(parts[1].trim(), 10) || 50;
                percent = Math.max(0, Math.min(100, (rem / tot) * 100));
            } else {
                percent = parseFloat(input.replace('%', '').trim()) || 80;
                rem = Math.round((percent / 100) * 50);
            }
            sendSync({
                remaining: percent,
                used_messages: Math.max(0, tot - rem),
                total_messages: tot,
                model: 'Grok (手動校準)',
                window_minutes: 120,
                resets_at: Math.floor(Date.now() / 1000) + 7200
            }, true);
        });

        GM_registerMenuCommand('🚀 立即測試連線 (滿額 100%)', function() {
            sendSync({
                remaining: 100.0,
                used_messages: 0,
                total_messages: 50,
                model: 'Grok 3 (測試)',
                window_minutes: 120,
                resets_at: Math.floor(Date.now() / 1000) + 7200
            }, true);
        });
    }

    log('TokenIsland Grok 1.1.0 監控注入完成');
})();
