// YaneuraOu WASM Engine wrapper for Blazor
// メインスレッドで直接実行（WASMがpthreadを使用するため）
(() => {
    'use strict';

    let engine = null;
    let dotNetReference = null;
    let scriptLoaded = false;
    let lastMessageTime = 0;
    let watchdogTimer = null;

    const WATCHDOG_TIMEOUT = 5000; // 5秒間応答がなければクラッシュとみなす

    /**
     * エンジンを初期化
     * @param {boolean} force - 強制再初期化
     * @returns {Promise<boolean>} 初期化成功時true
     */
    async function init(force = false) {
        if (engine && !force) {
            return true;
        }

        // 既存のエンジンを破棄
        if (engine) {
            stopWatchdog();
            try {
                engine.terminate?.();
            } catch (e) {
                // ignore
            }
            engine = null;
        }

        try {
            // YaneuraOuスクリプトを動的ロード（初回のみ）
            if (!scriptLoaded) {
                await loadScript('lib/yaneuraou/yaneuraou.js');
                scriptLoaded = true;
            }

            // WASMモジュールを初期化
            const yaneuraou = await YaneuraOu({
                locateFile: (path) => `lib/yaneuraou/${path}`
            });

            // メッセージリスナーを設定
            yaneuraou.addMessageListener((line) => {
                lastMessageTime = Date.now();
                dotNetReference?.invokeMethodAsync('OnEngineMessage', line);
            });

            engine = yaneuraou;

            // USIハンドシェイク（usiok待機）
            await waitForMessage('usiok', () => engine.postMessage('usi'));

            return true;
        } catch (error) {
            console.error('Failed to initialize shogi engine:', error);
            return false;
        }
    }

    /**
     * スクリプトを動的にロード
     */
    function loadScript(src) {
        return new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = src;
            script.onload = resolve;
            script.onerror = reject;
            document.head.appendChild(script);
        });
    }

    /**
     * 特定のメッセージを待機
     */
    function waitForMessage(expected, sendCommand) {
        return new Promise((resolve) => {
            const handler = (line) => {
                if (line === expected) {
                    engine.removeMessageListener(handler);
                    resolve();
                }
            };
            engine.addMessageListener(handler);
            sendCommand();
        });
    }

    /**
     * エンジンを再起動
     * @returns {Promise<boolean>} 再起動成功時true
     */
    async function restart() {
        console.log('Restarting shogi engine...');
        return await init(true);
    }

    /**
     * .NETコールバック参照を設定
     */
    function setCallback(dotNetRef) {
        dotNetReference = dotNetRef;
    }

    /**
     * エンジンにコマンドを送信
     * @param {string} command - USIコマンド
     */
    function sendCommand(command) {
        if (!engine) {
            return false;
        }
        engine.postMessage(command);
        return true;
    }

    /**
     * ウォッチドッグタイマーを開始
     */
    function startWatchdog() {
        lastMessageTime = Date.now();
        if (watchdogTimer) {
            clearInterval(watchdogTimer);
        }
        watchdogTimer = setInterval(() => {
            if (Date.now() - lastMessageTime > WATCHDOG_TIMEOUT) {
                console.error('Engine watchdog timeout - no response for', WATCHDOG_TIMEOUT, 'ms');
                stopWatchdog();
                dotNetReference?.invokeMethodAsync('OnEngineCrash');
            }
        }, 1000);
    }

    /**
     * ウォッチドッグタイマーを停止
     */
    function stopWatchdog() {
        if (watchdogTimer) {
            clearInterval(watchdogTimer);
            watchdogTimer = null;
        }
    }

    /**
     * Cross-Origin Isolationが有効か確認
     */
    function isCrossOriginIsolated() {
        return window.crossOriginIsolated === true;
    }

    // Blazor用にエクスポート
    window.ShogiEngine = {
        init,
        restart,
        setCallback,
        sendCommand,
        startWatchdog,
        stopWatchdog,
        isCrossOriginIsolated
    };
})();
