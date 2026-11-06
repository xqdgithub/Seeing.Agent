/**
 * Seeing.Agent WebUI — 语音输入
 *
 * 交互契约：点击开始录音，再点一次结束并转写（不是按住说话）。
 * 录音与上传都在浏览器侧完成；转写在服务端（/api/transcriptions）。
 *
 * 对外暴露的全局函数（由 PromptInput.razor 经 JSRuntime 调用）：
 *   startVoiceRecording(containerId, textareaId, dotNetRef, maxSeconds)
 *   stopVoiceRecording()
 *   cancelVoiceRecording()
 *   disposeVoiceInput()
 *   getVoiceInsertAnchor(textareaId)
 *   setVoiceCaret(textareaId, position)
 *
 * 回调 .NET 的方法：HandleVoiceTick / HandleVoiceTranscribed / HandleVoiceInterrupted
 */

// 模块级状态：同一时刻只允许一段录音。
var voiceRecorder = null;
var voiceStream = null;
var voiceChunks = [];
var voiceTimerId = null;
var voiceSeconds = 0;
var voiceMimeType = '';
var voiceCancelled = false;
var voiceDotNetRef = null;
var voiceContainerId = null;
// 起流中的重入守卫：getUserMedia 是异步的，期间 voiceRecorder 仍为 null，
// 没有这个标志时用户连点两次会拿到两条麦克风流，第一条永远没人 stop（指示灯常亮）。
var voiceStarting = false;

// 候选音频容器：按优先级探测，不硬编码 webm
// （WKWebView 只出得了 audio/mp4，写死 webm 会让 macOS 桌面版必然失败）。
var VOICE_MIME_CANDIDATES = [
    'audio/webm;codecs=opus',
    'audio/webm',
    'audio/mp4',
    'audio/ogg;codecs=opus'
];

/** 安全地调用 .NET 回调：circuit 可能已断开，不能让异常冒泡到录音流程。 */
async function invokeVoiceCallback(method, ...args) {
    if (!voiceDotNetRef) return;
    try {
        await voiceDotNetRef.invokeMethodAsync(method, ...args);
    } catch (err) {
        console.error('voice callback failed: ' + method, err);
    }
}

/** 从 MIME 推导文件扩展名。 */
function voiceExtensionFor(mimeType) {
    var mime = (mimeType || '').toLowerCase();
    if (mime.indexOf('webm') >= 0) return 'webm';
    if (mime.indexOf('mp4') >= 0) return 'mp4';
    if (mime.indexOf('ogg') >= 0) return 'ogg';
    if (mime.indexOf('wav') >= 0) return 'wav';
    if (mime.indexOf('mpeg') >= 0) return 'mp3';
    // 不产出 .bin：ASR 服务端多按扩展名识别容器，.bin 会被直接 400。
    // 走到这里说明浏览器连默认类型都没给，按 webm 兜底（Chrome/Edge/Firefox 的默认容器）。
    return 'webm';
}

/** 挑第一个浏览器支持的容器；全不支持则返回空串，交给 MediaRecorder 用默认值。 */
function pickVoiceMimeType() {
    if (!window.MediaRecorder || typeof MediaRecorder.isTypeSupported !== 'function') return '';
    for (var i = 0; i < VOICE_MIME_CANDIDATES.length; i++) {
        if (MediaRecorder.isTypeSupported(VOICE_MIME_CANDIDATES[i])) return VOICE_MIME_CANDIDATES[i];
    }
    return '';
}

/** 释放麦克风：指示灯必须在转写开始前熄灭。 */
function releaseVoiceStream() {
    if (!voiceStream) return;
    try {
        voiceStream.getTracks().forEach(function (track) { track.stop(); });
    } catch (err) {
        console.error('stop track failed', err);
    }
    voiceStream = null;
}

function clearVoiceTimer() {
    if (voiceTimerId !== null) {
        clearInterval(voiceTimerId);
        voiceTimerId = null;
    }
}

/**
 * 开始录音。返回 { ok } 或 { ok:false, code, message }。
 */
async function startVoiceRecording(containerId, textareaId, dotNetRef, maxSeconds) {
    if (voiceRecorder !== null || voiceStarting) {
        return { ok: false, code: 'alreadyRecording', message: '已经在录音中' };
    }

    // 安全上下文是浏览器的硬性要求：非 https 且非 localhost 时，
    // 浏览器根本不暴露 navigator.mediaDevices，若不先判这一条，
    // 下面那个「不支持录音」分支会先命中，把「http 访问」误报成「浏览器太旧」。
    // isSecureContext 是浏览器的权威判定，已内含 localhost / 127.0.0.1 / [::1] 例外，
    // 无需再手写地址白名单（手写列表还会漏掉 *.localhost 子域等情形）。
    if (!window.isSecureContext) {
        return {
            ok: false,
            code: 'insecureContext',
            message: '浏览器要求 https 或 localhost 才能使用麦克风（用局域网 IP 访问时需要 https）'
        };
    }

    if (!navigator.mediaDevices || typeof navigator.mediaDevices.getUserMedia !== 'function') {
        return { ok: false, code: 'unsupportedBrowser', message: '当前浏览器不支持录音' };
    }
    if (!window.MediaRecorder) {
        return { ok: false, code: 'unsupportedBrowser', message: '当前浏览器不支持录音' };
    }

    voiceStarting = true;

    try {
        voiceStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch (err) {
        voiceStarting = false;
        if (err && err.name === 'NotAllowedError') {
            return { ok: false, code: 'permissionDenied', message: '麦克风权限被拒绝' };
        }
        if (err && err.name === 'NotFoundError') {
            return { ok: false, code: 'noDevice', message: '未找到麦克风设备' };
        }
        return {
            ok: false,
            code: 'deviceError',
            message: '无法打开麦克风：' + (err && err.message ? err.message : err)
        };
    }

    voiceDotNetRef = dotNetRef;
    voiceContainerId = containerId;
    voiceChunks = [];
    voiceCancelled = false;
    voiceSeconds = 0;

    var mimeType = pickVoiceMimeType();
    voiceMimeType = mimeType;

    try {
        voiceRecorder = mimeType
            ? new MediaRecorder(voiceStream, { mimeType: mimeType })
            : new MediaRecorder(voiceStream);
    } catch (err) {
        voiceStarting = false;
        releaseVoiceStream();
        voiceRecorder = null;
        return {
            ok: false,
            code: 'unsupportedBrowser',
            message: '无法初始化录音器：' + (err && err.message ? err.message : err)
        };
    }

    // 浏览器可能把我们请求的容器降级成别的；以实际值为准。
    if (voiceRecorder.mimeType) voiceMimeType = voiceRecorder.mimeType;

    voiceStarting = false;

    voiceRecorder.ondataavailable = function (e) {
        if (e.data && e.data.size > 0) voiceChunks.push(e.data);
    };

    // 设备被占用/拔出等硬错误：录音已经无法继续，交给 .NET 复位状态。
    voiceRecorder.onerror = function (e) {
        var message = e && e.error && e.error.message ? e.error.message : '录音被中断';
        releaseVoiceStream();
        clearVoiceTimer();
        voiceRecorder = null;
        voiceChunks = [];
        settleVoiceStop();
        invokeVoiceCallback('HandleVoiceInterrupted', message);
    };

    voiceRecorder.onstop = function () {
        // 先熄灯：麦克风指示灯不能在转写期间还亮着。
        releaseVoiceStream();
        clearVoiceTimer();

        if (voiceCancelled) {
            voiceChunks = [];
            voiceRecorder = null;
            settleVoiceStop();
            return;
        }

        var blob = new Blob(voiceChunks, { type: voiceMimeType || 'application/octet-stream' });
        voiceChunks = [];
        voiceRecorder = null;

        // 上传在后台继续；stopVoiceRecording 返回的 Promise 等它结束。
        voiceUpload(blob, voiceMimeType, voiceContainerId)
            .then(settleVoiceStop, settleVoiceStop);
    };

    voiceRecorder.start();

    var limit = typeof maxSeconds === 'number' && maxSeconds > 0 ? maxSeconds : 600;
    // 用时间差而非自增：后台标签页 setInterval 会被浏览器节流到约 1 次/分钟，
    // 自增会让"10 分钟上限"实际拖到远超 10 分钟，进而逼近 25MB 请求上限。
    var startedAt = Date.now();
    voiceTimerId = setInterval(function () {
        voiceSeconds = Math.floor((Date.now() - startedAt) / 1000);
        invokeVoiceCallback('HandleVoiceTick', voiceSeconds);
        if (voiceSeconds >= limit) {
            stopVoiceRecording();
        }
    }, 1000);

    return { ok: true, mimeType: voiceMimeType };
}

/** 上传结果 Promise：stopVoiceRecording 等它 resolve。 */
var voiceStopResolve = null;

/** onstop 流程结束时调用，放行 stopVoiceRecording 的等待。 */
function settleVoiceStop() {
    if (voiceStopResolve) {
        var resolve = voiceStopResolve;
        voiceStopResolve = null;
        resolve();
    }
}

async function voiceUpload(blob, mimeType, containerId) {
    var container = containerId ? document.getElementById(containerId) : null;
    var sessionId = container && container.dataset ? container.dataset.sessionId : null;

    if (!sessionId) {
        await invokeVoiceCallback('HandleVoiceTranscribed', null, '当前会话未就绪，无法提交语音', 'invalidRequest');
        return;
    }

    var form = new FormData();
    form.append('audio', blob, 'voice.' + voiceExtensionFor(mimeType));

    try {
        var response = await fetch('/api/transcriptions?sessionId=' + encodeURIComponent(sessionId), {
            method: 'POST',
            headers: { 'X-Seeing-Voice': '1' },
            body: form
        });

        var body = null;
        try { body = await response.json(); } catch (e) { body = null; }

        if (!response.ok) {
            var message = body && body.error ? body.error : '转写失败（HTTP ' + response.status + '）';
            var code = body && body.code ? body.code : 'transcriptionFailed';
            await invokeVoiceCallback('HandleVoiceTranscribed', null, message, code);
            return;
        }

        var text = body && typeof body.text === 'string' ? body.text : '';
        await invokeVoiceCallback('HandleVoiceTranscribed', text, null, null);
    } catch (err) {
        await invokeVoiceCallback(
            'HandleVoiceTranscribed',
            null,
            '转写请求失败：' + (err && err.message ? err.message : err),
            'networkError');
    }
}

/** 结束录音并上传；Promise 在上传完成后 resolve。 */
function stopVoiceRecording() {
    if (voiceRecorder === null) return Promise.resolve();

    clearVoiceTimer();

    var completed = new Promise(function (resolve) {
        voiceStopResolve = resolve;
    });

    try {
        if (voiceRecorder.state !== 'inactive') voiceRecorder.stop();
    } catch (err) {
        console.error('stop recorder failed', err);
        settleVoiceStop();
    }

    return completed;
}

/** 取消录音并丢弃，不上传。 */
function cancelVoiceRecording() {
    if (voiceRecorder === null) {
        releaseVoiceStream();
        clearVoiceTimer();
        settleVoiceStop();
        return;
    }

    voiceCancelled = true;
    clearVoiceTimer();

    try {
        if (voiceRecorder.state !== 'inactive') voiceRecorder.stop();
        else settleVoiceStop();
    } catch (err) {
        console.error('cancel recorder failed', err);
        settleVoiceStop();
    }
}

/** 组件释放时清理：停计时器、熄灯、断引用。 */
function disposeVoiceInput() {
    cancelVoiceRecording();
    clearVoiceTimer();
    releaseVoiceStream();
    voiceChunks = [];
    voiceDotNetRef = null;
    voiceContainerId = null;
    settleVoiceStop();
}

/** 取 textarea 当前选区，作为插入锚点。找不到元素时返回文末。 */
function getVoiceInsertAnchor(textareaId) {
    var textarea = document.getElementById(textareaId);
    if (!textarea) return { start: 0, end: 0 };

    var start = typeof textarea.selectionStart === 'number' ? textarea.selectionStart : textarea.value.length;
    var end = typeof textarea.selectionEnd === 'number' ? textarea.selectionEnd : start;
    return { start: start, end: end };
}

/** 插入完成后把光标放回指定位置并聚焦。 */
function setVoiceCaret(textareaId, position) {
    var textarea = document.getElementById(textareaId);
    if (!textarea) return;

    try {
        textarea.focus();
        var pos = Math.max(0, Math.min(position, textarea.value.length));
        textarea.setSelectionRange(pos, pos);
    } catch (err) {
        console.error('set caret failed', err);
    }
}
