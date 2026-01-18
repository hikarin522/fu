// YaneuraOu WASM Engine wrapper for Blazor
(() => {
    'use strict';

    let engine = null;
    let dotNetReference = null;

    /**
     * エンジンを初期化し、USIハンドシェイクを完了する
     * @returns {Promise<boolean>} 初期化成功時true
     */
    async function init() {
        if (engine) {
            return true;
        }

        try {
            // YaneuraOuスクリプトを動的ロード
            await loadScript('lib/yaneuraou/yaneuraou.js');

            // WASMモジュールを初期化
            const yaneuraou = await YaneuraOu({
                locateFile: (path) => `lib/yaneuraou/${path}`
            });

            // メッセージリスナーを設定
            yaneuraou.addMessageListener((line) => {
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
     * .NETコールバック参照を設定
     */
    function setCallback(dotNetRef) {
        dotNetReference = dotNetRef;
    }

    /**
     * エンジンにコマンドを送信
     */
    function sendCommand(command) {
        if (!engine) {
            return false;
        }
        engine.postMessage(command);
        return true;
    }

    /**
     * 局面の評価をリクエスト
     * stop → position → go を一括実行
     */
    function requestEvaluation(sfen, depth) {
        if (!engine) {
            return false;
        }

        engine.postMessage('stop');
        engine.postMessage('position sfen ' + sfen);
        engine.postMessage(depth > 0 ? `go depth ${depth}` : 'go infinite');

        return true;
    }

    /**
     * 探索を停止
     */
    function stop() {
        if (!engine) {
            return false;
        }
        engine.postMessage('stop');
        return true;
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
        setCallback,
        sendCommand,
        requestEvaluation,
        stop,
        isCrossOriginIsolated
    };
})();
