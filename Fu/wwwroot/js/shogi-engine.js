// YaneuraOu WASM Engine wrapper for Blazor
// 純粋な転送層 - WebWorkerとC#間のメッセージを中継するだけ
(() => {
    'use strict';

    let worker = null;
    let dotNetReference = null;
    let initPromise = null;
    let isReady = false;

    /**
     * ワーカーを作成してエンジンを初期化
     * @param {boolean} force - 強制再初期化
     * @returns {Promise<boolean>} 初期化成功時true
     */
    async function init(force = false) {
        if (worker && !force) {
            return isReady;
        }

        // 既存のワーカーを終了
        if (worker) {
            worker.terminate();
            worker = null;
        }

        isReady = false;

        try {
            // 新しいワーカーを作成
            worker = new Worker('js/shogi-engine-worker.js');

            // メッセージハンドラを設定
            worker.onmessage = (e) => {
                const { type, data } = e.data;

                switch (type) {
                    case 'message':
                        // エンジンからのメッセージをそのままC#に転送
                        dotNetReference?.invokeMethodAsync('OnEngineMessage', data);
                        break;

                    case 'init':
                        // initPromiseのresolveで処理される
                        break;

                    case 'crash':
                    case 'error':
                        console.error('Engine worker crashed:', data);
                        isReady = false;
                        dotNetReference?.invokeMethodAsync('OnEngineCrash');
                        break;
                }
            };

            // ワーカーエラーハンドラ（ワーカー自体のクラッシュを検出）
            worker.onerror = (error) => {
                console.error('Worker error:', error);
                isReady = false;
                dotNetReference?.invokeMethodAsync('OnEngineCrash');
            };

            // 初期化を待機
            initPromise = new Promise((resolve) => {
                const handler = (e) => {
                    if (e.data.type === 'init') {
                        worker.removeEventListener('message', handler);
                        resolve(e.data.data);
                    }
                };
                worker.addEventListener('message', handler);
                worker.postMessage({ type: 'init' });
            });

            isReady = await initPromise;
            return isReady;
        } catch (error) {
            console.error('Failed to create engine worker:', error);
            return false;
        }
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
     */
    function sendCommand(command) {
        if (!worker || !isReady) {
            return false;
        }
        worker.postMessage({ type: 'command', data: command });
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
        restart,
        setCallback,
        sendCommand,
        isCrossOriginIsolated
    };
})();
