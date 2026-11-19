// 浏览器工作台前端：WebSocket 连接、二进制帧绘制到 canvas、鼠标/键盘采集与坐标换算。
//
// 为什么用 canvas + WebSocket 而不是 <img> + SignalR：
//   帧是二进制、每秒可达数十帧；走 Blazor circuit 会让服务端渲染全面卡顿。
//   SignalR 单消息也有大小限制，JPEG 很容易超。
//
// 消息分两类（同一条 WebSocket）：
//   ① 二进制 = 画面帧（18 字节头 + JPEG，见 BrowserFrameCodec）；
//   ② 文本 JSON = 控制消息（state / error / takeover）。
// 帧比状态多得多，所以帧走二进制、省掉 base64 与 JSON 两层编解码。

const states = new Map();

/// 帧头长度（字节）。与服务端 BrowserFrameCodec.HeaderSize 必须一致。
const FRAME_HEADER_BYTES = 18;

/// 帧种类（与服务端 BrowserFrameKind 一致）。
const FRAME_KIND_FLOW = 1;
const FRAME_KIND_STILL = 2;

const DEFAULTS = {
    minBackoffMs: 1000,
    maxBackoffMs: 15000,
    // 拖动面板时 ResizeObserver 连续触发；每次 resize 都要服务端重建 CDP screencast，
    // 所以必须去抖（150ms 足够让拖拽结束、又不影响手感）
    resizeDebounceMs: 150,
    // 画面静止多久后请求一张静帧补清晰度（普通模式帧只有 CSS 分辨率）
    stillDebounceMs: 200,
    // 静帧在途超时：抓一张最多 800ms，这里给足余量；超时后清在途，否则
    // 一次失败（或丢包）会让「永远不再请求静帧」，画面永久停在模糊帧
    stillPendingTimeoutMs: 2000,
    // 画布内点击到 viewport 坐标的换算依赖这个状态（服务端 state 到达前的占位值）
    viewportWidth: 1280,
    viewportHeight: 800
};

function getState(host) {
    let state = states.get(host);
    if (!state) {
        state = {
            host,
            canvas: null,
            ctx: null,
            dotnet: null,
            socket: null,
            url: null,
            reconnectTimer: null,
            backoffMs: DEFAULTS.minBackoffMs,
            closedByUser: false,
            opened: false,
            closedReason: null,
            closedReasonKind: null,
            // ── 帧协议相关 ──
            // 画面代次（state 与帧头同源）：代次一变，帧序号从头开始，旧序号不能再参与比较
            epoch: 0,
            // 流帧序号：仲裁流帧（seq <= lastFlowSeq 丢弃）与静帧（rev < lastFlowSeq 丢弃）
            lastFlowSeq: 0,
            lastFrameSeq: 0,
            lastDrawnSeq: 0,
            droppedFrames: 0,
            // 坐标与画布 backing 的权威来源（服务端实际 SetViewport 的 CSS 尺寸）
            viewportWidth: DEFAULTS.viewportWidth,
            viewportHeight: DEFAULTS.viewportHeight,
            // 帧像素 / 视口 CSS：普通模式 = dpr，超采样模式 = 1
            frameScale: 1,
            // normal | highres（超采样）；highres 下不请求静帧
            mode: 'normal',
            frameWidth: 0,
            frameHeight: 0,
            // 最近一帧的种类（2 = 静帧）：仅诊断/排查用
            lastFrameKind: 0,
            // ── 静帧请求 ──
            stillTimer: null,
            // 在途静帧的发起时刻（performance.now()）；0 = 没有请求在途
            hiresPendingAt: 0,
            hiresTimer: null,
            // 已被静帧覆盖到的 rev：小于 lastFlowSeq 说明当前画面还缺一张清晰图
            stillCoveredRev: 0,
            modifiers: { ctrl: false, alt: false, shift: false, meta: false },
            resizeTimer: null,
            lastSentResizeWidth: 0,
            lastSentResizeHeight: 0,
            lastSentDpr: 0,
            resizeObserver: null,
            // 拖动状态：拖动中 mousemove 需按帧合并，且坐标允许越界钳到边界。
            dragging: false,
            pendingMove: null,
            moveRaf: 0,
            lastPoint: null
        };
        states.set(host, state);
    }
    return state;
}

// ── 帧绘制 ───────────────────────────────────────────────────────────
//
// 用 createImageBitmap 解码，避免每帧都从 base64 构造 <img> 元素（后者会触发 DOM 布局）。
// 帧是二进制（18 字节头 + JPEG），JSON/base64 那一层已经在服务端去掉。
// 解码失败只记数，不打断连接：个别帧坏掉不该让画面永久黑屏。

function stageRect(state) {
    const stage = state.stage;
    if (!stage) return null;
    const rect = stage.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0 ? rect : null;
}

/// 按 contain 把画布的**显示尺寸**设置为「帧比例放进 stage」的结果。
///
/// 关键：比例只由帧决定，与 stage 无关 —— 所以无论面板怎样变，
/// 画面宽高比都严格等于原网页，永不拉伸。
function applyCanvasFit(state) {
    const canvas = state.canvas;
    const rect = stageRect(state);
    if (!canvas || !rect) return;

    const bmpW = canvas.width;
    const bmpH = canvas.height;
    if (bmpW <= 0 || bmpH <= 0) return;

    // contain：取较小的缩放比，保证整幅画面都看得见。
    // 不取 min(1, scale)：面板比网页大时适度放大，否则小视口页面会缩成一小块。
    const scale = Math.min(rect.width / bmpW, rect.height / bmpH);
    canvas.style.width = `${Math.max(1, Math.round(bmpW * scale))}px`;
    canvas.style.height = `${Math.max(1, Math.round(bmpH * scale))}px`;
}

/// 从二进制消息里解析帧头并仲裁，通过后交给 drawFrame。
///
/// 头布局（小端，18 字节）：kind u8 | version u8 | epoch u32 | seq u32 | width u16 | height u16 | length u32
function handleFrameBinary(state, buffer) {
    if (buffer.byteLength < FRAME_HEADER_BYTES) return;

    const dv = new DataView(buffer);
    const kind = dv.getUint8(0);
    const version = dv.getUint8(1);
    const epoch = dv.getUint32(2, true);
    const seq = dv.getUint32(6, true);
    const w = dv.getUint16(10, true);
    const h = dv.getUint16(12, true);
    const len = dv.getUint32(14, true);

    // 版本不认识、或长度与头里写的不符（截断/错位）都不渲染：
    // 半张图比不画更糟，会让用户以为页面就是这样。
    if (version !== 1 || FRAME_HEADER_BYTES + len !== dv.byteLength) return;

    // 代次变化（换页面 / 重建 screencast）→ 帧序号从头开始，必须清掉旧的比较基准，
    // 否则新代次的前若干帧会被当成「过期帧」丢掉，画面停在上一次的画面。
    if (epoch !== state.epoch) {
        state.epoch = epoch;
        state.lastFlowSeq = 0;
        state.lastDrawnSeq = 0;
        state.lastFrameSeq = 0;
        // 旧代次的静帧请求已经失效（rev 属于上一代次），别让「在途」挡着新请求
        clearHiresPending(state);
        state.stillCoveredRev = 0;
    }

    if (kind === FRAME_KIND_FLOW) {
        // 流帧：seq 单调递增，重复/回退的丢弃
        if (seq <= state.lastFlowSeq) return;
        state.lastFlowSeq = seq;
        state.lastFrameSeq = seq;
        // 画面在动：推迟静帧请求（静止 200ms 后才值得抓一张清晰图）
        markActivity(state);
    } else if (kind === FRAME_KIND_STILL) {
        // 静帧：seq 承载 rev（发起请求时的 lastFlowSeq）。
        // 收到即清在途 —— 无论这张图是否已过期，服务端都已回应过本次请求。
        clearHiresPending(state);
        if (seq < state.lastFlowSeq) {
            // rev < lastFlowSeq 说明请求之后画面又动过：这张图比当前画面旧，丢弃；
            // 且**不**推进 lastFlowSeq（静帧不是流的一环）。
            // 但当前画面仍缺一张清晰图 → 重新去抖，静止后再要一张。
            markActivity(state);
            return;
        }
        state.stillCoveredRev = seq;
    } else {
        return;     // 未知种类：不猜，直接丢
    }

    void drawFrame(state, new Uint8Array(dv.buffer, FRAME_HEADER_BYTES, len), w, h, seq, kind);
}

async function drawFrame(state, bytes, width, height, seq, kind) {
    const canvas = state.canvas;
    if (!canvas) return;

    // 帧是并发到达的：解码慢的旧帧可能后完成并把新画面覆盖掉（页面静止时更明显，
    // 因为静止后不再出新帧，画布就长期停在旧画面）。用 seq 丢弃过期帧。
    if (typeof seq === 'number' && seq < (state.lastDrawnSeq || 0)) return;

    try {
        const bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/jpeg' }));

        // 解码期间可能有更新的帧已经开始绘制：再判一次，避免旧帧最后落地
        if (typeof seq === 'number' && seq < (state.lastDrawnSeq || 0)) {
            bitmap.close?.();
            return;
        }

        const bmpW = bitmap.width;
        const bmpH = bitmap.height;
        if (bmpW === 0 || bmpH === 0) {
            bitmap.close?.();
            return;
        }

        // backing（画布的位图尺寸）= round(视口 × 帧倍率)，即「物理像素」。
        // 普通模式下帧只有 CSS 尺寸（frameScale = dpr），静帧才是物理像素：
        // 只有 backing 按物理像素定，静帧才能 1:1 落地、文字才清晰。
        //
        // ⚠ 只在值变化时赋值：给 canvas.width 赋值会重置整个画布（清空内容 + 重置上下文状态），
        // 逐帧赋值就会逐帧闪黑。
        const vw = state.viewportWidth > 0 ? state.viewportWidth : bmpW;
        const vh = state.viewportHeight > 0 ? state.viewportHeight : bmpH;
        const scale = state.frameScale > 0 ? state.frameScale : 1;
        const targetW = Math.max(1, Math.round(vw * scale));
        const targetH = Math.max(1, Math.round(vh * scale));

        if (canvas.width !== targetW || canvas.height !== targetH) {
            canvas.width = targetW;
            canvas.height = targetH;
        }

        const ctx = state.ctx || canvas.getContext('2d');
        state.ctx = ctx;
        ctx.clearRect(0, 0, targetW, targetH);

        // 帧可能比 backing 小（普通模式的流帧）：缩放到铺满，否则右下角留黑边。
        // 帧比 backing 大（视口刚变、state 还没跟上）时同样缩放：等比拉伸比留白可接受。
        if (bmpW === targetW && bmpH === targetH) {
            ctx.drawImage(bitmap, 0, 0);
        } else {
            ctx.drawImage(bitmap, 0, 0, targetW, targetH);
        }
        bitmap.close?.();

        // 旧实现是 CSS `width/height:100%` 铺满 + 位图按帧尺寸：
        // 一旦帧的宽高比与面板不一致（视口被钳制、或 resize 尚未生效），
        // 画面就会被横向/纵向拉长 —— 用户报的「拉伸铺满导致变形」。
        applyCanvasFit(state);

        if (typeof seq === 'number') state.lastDrawnSeq = seq;

        state.frameWidth = width || bmpW;
        state.frameHeight = height || bmpH;
        state.lastFrameKind = kind;

        // 坐标换算的权威来源是 state.viewportWidth/Height（服务端实际 SetViewport 的 CSS 尺寸），
        // **不是**帧的位图像素：普通模式下帧是 CSS 尺寸、显示是物理像素（dpr），
        // 超采样模式下视口本身已经乘过 dpr。拿帧像素当量程会让点击坐标系统性偏移。
    } catch {
        state.droppedFrames++;
    }
}

// ── 静帧请求（仅普通模式）──────────────────────────────────────────
//
// 普通模式下持续流只有 CSS 分辨率：在高 DPI 屏上把 backing 放大到物理像素后，
// 流帧被拉伸 → 文字发虚。画面静止 200ms 后向服务端要一张物理像素的静帧补上。
//
// 超采样模式（mode === 'highres'）下滑本身已是物理像素，再抓静帧纯属浪费。

/// 记录一次「画面在动」：重置静帧去抖窗口。
function markActivity(state) {
    if (state.mode !== 'normal') return;

    if (state.stillTimer !== null) clearTimeout(state.stillTimer);

    state.stillTimer = setTimeout(() => {
        state.stillTimer = null;
        requestStill(state);
    }, DEFAULTS.stillDebounceMs);
}

function clearHiresPending(state) {
    state.hiresPendingAt = 0;
    if (state.hiresTimer !== null) {
        clearTimeout(state.hiresTimer);
        state.hiresTimer = null;
    }
}

function requestStill(state) {
    if (state.mode !== 'normal') return;
    if (!state.socket || state.socket.readyState !== WebSocket.OPEN) return;
    if (state.lastFlowSeq <= 0) return;     // 还没有画面，没有可对应的 rev

    // 已有请求在途：不排队也不重发（服务端也是单飞，重发只会被丢掉）。
    // 在途那张回来时若已过期（rev < lastFlowSeq），由 handleFrameBinary 重新去抖；
    // 若超时没回来，由下面的超时分支重新去抖 —— 需求不会丢。
    if (state.hiresPendingAt !== 0) return;

    if (!sendRaw(state, { type: 'hires', rev: state.lastFlowSeq })) return;

    state.hiresPendingAt = performance.now();

    // 在途超时：抓图失败/消息丢失时不能让「在途」永久挂住，
    // 否则之后再也不会有静帧（画面永久模糊，且无任何报错）。
    if (state.hiresTimer !== null) clearTimeout(state.hiresTimer);
    state.hiresTimer = setTimeout(() => {
        state.hiresTimer = null;
        state.hiresPendingAt = 0;

        // 当前画面还没有被任何静帧覆盖 → 这次失败要补回来（重新去抖）
        if (state.stillCoveredRev < state.lastFlowSeq) markActivity(state);
    }, DEFAULTS.stillPendingTimeoutMs);
}

// ── WebSocket ────────────────────────────────────────────────────────

function scheduleReconnect(state) {
    if (state.closedByUser) return;
    if (state.reconnectTimer) return;

    const delay = state.backoffMs;
    state.reconnectTimer = setTimeout(() => {
        state.reconnectTimer = null;
        connect(state);
    }, delay);

    state.backoffMs = Math.min(state.backoffMs * 2, DEFAULTS.maxBackoffMs);

    // 首次握手就失败时保留可行动原因（例如证书未信任），只在后面附上重试倒计时。
    // 无条件覆盖成「正在重连（N 秒后）」会让用户永远看不出根因、只能一直等。
    if (state.closedReason) {
        notifyStatus(state, state.closedReasonKind || 'error',
            `${state.closedReason}（${Math.round(delay / 1000)} 秒后重试）`);
    } else {
        notifyStatus(state, 'reconnecting', `正在重连（${Math.round(delay / 1000)} 秒后）`);
    }
}

function buildSocketUrl(streamUrl) {
    // 相对路径转绝对：ws(s)://host + path
    if (streamUrl.startsWith('ws://') || streamUrl.startsWith('wss://')) return streamUrl;

    const scheme = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const base = streamUrl.startsWith('/') ? streamUrl : '/' + streamUrl;
    return `${scheme}//${window.location.host}${base}`;
}

function connect(state) {
    if (!state.url) return;

    try {
        state.opened = false;   // 每次重连重新判定「本次是否握手成功」
        const socket = new WebSocket(buildSocketUrl(state.url));
        socket.binaryType = 'arraybuffer';
        state.socket = socket;

        socket.onopen = () => {
            state.backoffMs = DEFAULTS.minBackoffMs;
            state.opened = true;
            state.closedReason = null;
            state.closedReasonKind = null;
            notifyStatus(state, 'connected', '已连接');
            // 面板当前尺寸推给服务端，避免首帧按默认视口渲染后拉伸
            sendResize(state);
        };

        socket.onmessage = (event) => {
            // 二进制 = 画面帧（kind/epoch/seq/w/h/len + JPEG）
            if (event.data instanceof ArrayBuffer) {
                handleFrameBinary(state, event.data);
                return;
            }

            // 其余是控制消息（JSON 文本），帧不会再走这条路
            let message;
            try {
                message = JSON.parse(event.data);
            } catch {
                return;
            }

            switch (message.type) {
                case 'state':
                    // 页面状态回来了（导航成功、操作完成）：把之前那条错误提示清掉。
                    // 否则「输入被拒」的红字会一直挂在状态栏，即使后续导航已经成功 ——
                    // 用户会以为工具栏坏了（实测症状）。
                    if (state.closedReasonKind === 'error') {
                        state.closedReason = null;
                        state.closedReasonKind = null;
                    }
                    applyStateMessage(state, message);
                    notifyState(state, message);
                    // 只在「当前不是重连/断开」时恢复成已连接
                    if (state.socket && state.socket.readyState === WebSocket.OPEN) {
                        notifyStatus(state, 'connected', '已连接');
                    }
                    break;

                case 'error':
                    notifyStatus(state, 'error', message.message || '浏览器出错');
                    break;

                case 'takeover':
                    // 服务端确认接管：通知 Blazor 解除遮罩并提示结果
                    if (state.dotnet) {
                        state.dotnet.invokeMethodAsync(
                            'OnTakeoverResult',
                            message.interrupted === true,
                            typeof message.actionCount === 'number' ? message.actionCount : 0
                        ).catch(() => { });
                    }
                    break;
            }
        };

        socket.onclose = () => {
            state.socket = null;

            // 在途的静帧请求永远不会回来了：立刻清掉，
            // 否则重连后要等 2 秒超时才允许再次请求（这段时间画面一直是模糊的）。
            clearHiresPending(state);

            // 一次都没连上就断开：说明握手阶段就失败了（不是连上后掉线）。
            // 注意 **不要**猜成「证书问题」——实测过：HTTPS 下真正的元凶是
            // Kestrel 协商到 HTTP/2、浏览器发 extended CONNECT，而服务端不支持，
            // 与证书是否受信任无关（同一页面里 SignalR 的 wss 连接是正常的）。
            // 所以这里只说事实 + 指路，不做错误归因。
            if (state.opened) {
                state.closedReason = null;
                notifyStatus(state, 'closed', '连接已断开，正在重连……');
            } else {
                state.closedReason = '无法建立画面流连接';
                state.closedReasonKind = 'error';
                notifyStatus(state, 'error', state.closedReason);
            }

            scheduleReconnect(state);
        };

        socket.onerror = () => {
            // onerror 后必有 onclose；这里不重复提示（onclose 会给可行动原因）
        };
    } catch (ex) {
        notifyStatus(state, 'error', '无法建立连接');
        scheduleReconnect(state);
    }
}

function sendRaw(state, payload) {
    if (!state.socket || state.socket.readyState !== WebSocket.OPEN) return false;
    state.socket.send(JSON.stringify(payload));
    return true;
}

/// 立刻发出待提交的移动（用于 down/up 之前保证顺序）。
///
/// 不这样做的话，rAF 里攒着的那一帧会在 down 之后才发出去：
/// 页面收到「按下 → 移动到按下点」的错序序列，滑块组件会认为用户松手了或者判定失败。
function FlushPendingMove(state) {
    if (state.moveRaf !== 0) {
        cancelAnimationFrame(state.moveRaf);
        state.moveRaf = 0;
    }

    const p = state.pendingMove;
    state.pendingMove = null;
    if (p) sendRaw(state, { type: 'mouse', event: 'move', x: p.x, y: p.y });
}

/// 丢弃待提交的移动（按下时用：避免旧位置污染新一轮拖动）。
function CancelPendingMove(state) {
    if (state.moveRaf !== 0) {
        cancelAnimationFrame(state.moveRaf);
        state.moveRaf = 0;
    }
    state.pendingMove = null;
}

function notifyStatus(state, kind, text) {
    if (!state.dotnet) return;
    state.dotnet.invokeMethodAsync('OnStatusChanged', kind, text).catch(() => { });
}

function notifyState(state, message) {
    if (!state.dotnet) return;
    state.dotnet.invokeMethodAsync(
        'OnBrowserState',
        message.url || '',
        message.title || '',
        message.busy === true,
        message.running !== false).catch(() => { });
}

/// 应用服务端下发的画面参数（epoch / 视口 / 帧倍率 / 模式）。
///
/// 这里**不动画布**：改 backing 会清空画面，而页面可能正好是静止的（没有新帧来重画），
/// 表现为「点一下画面就黑了」。backing 由下一帧（drawFrame）在新尺寸上重设。
function applyStateMessage(state, message) {
    const epoch = typeof message.epoch === 'number' ? message.epoch : 0;

    // epoch = 0 表示「未知」（本会话还没画面流）：不得当权威代次比较，
    // 否则每次收到这种 state 都会把帧序号基准清零（画面会反复定格）。
    if (epoch !== 0 && epoch !== state.epoch) {
        state.epoch = epoch;
        state.lastFlowSeq = 0;
        state.lastDrawnSeq = 0;
        state.lastFrameSeq = 0;
        clearHiresPending(state);
        state.stillCoveredRev = 0;
    }

    // 视口 = 服务端实际 SetViewport 的 CSS 尺寸（超采样模式下是面板 × dpr）。
    // 坐标换算与画布 backing 都以它为准；0 是「未知」，保留上一次已知值。
    if (typeof message.viewportWidth === 'number' && message.viewportWidth > 0) {
        state.viewportWidth = message.viewportWidth;
    }
    if (typeof message.viewportHeight === 'number' && message.viewportHeight > 0) {
        state.viewportHeight = message.viewportHeight;
    }
    if (typeof message.frameScale === 'number' && message.frameScale > 0) {
        state.frameScale = message.frameScale;
    }
    if (typeof message.mode === 'string' && message.mode.length > 0) {
        state.mode = message.mode;
    }

    // 超采样下流本身就是物理像素：没有「补一张清晰图」的需求，把待发的请求撤掉
    if (state.mode !== 'normal') {
        if (state.stillTimer !== null) {
            clearTimeout(state.stillTimer);
            state.stillTimer = null;
        }
        clearHiresPending(state);
    }
}

// ── 坐标换算 ─────────────────────────────────────────────────────────
//
// clampToEdge = true：坐标超出画面时钳到边界（拖动中必须如此，否则指针一越界就丢事件，
// 滑块会卡住不动 —— 用户报的「验证码拖不动」就是这个）。
// clampToEdge = false：超界返回 null（普通移动不需要把越界坐标送进页面）。

function toViewportPoint(state, event, clampToEdge) {
    const canvas = state.canvas;
    if (!canvas) return null;

    // 用 canvas 自身的显示矩形：它的 CSS 尺寸是 drawFrame 按 contain 显式算出的
    // （等比、居中由 stage 的 flex 居中完成），所以矩形就等于画面区域，
    // 不含任何留白 —— 不需要再减偏移。
    const rect = canvas.getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0) return null;

    // 用 **CSS 视口尺寸**（sendResize 推给服务端的那个值），不是帧的物理像素。
    // 服务端 SetViewport 收的是 CSS 像素；帧可能是它的 dpr 倍（高 DPI 屏）。
    // 早期把帧像素当视口尺寸用，在 2x 屏上所有点击坐标都会偏一倍。
    const vw = state.viewportWidth || rect.width;
    const vh = state.viewportHeight || rect.height;

    const rawX = ((event.clientX - rect.left) / rect.width) * vw;
    const rawY = ((event.clientY - rect.top) / rect.height) * vh;

    if (!clampToEdge && (rawX < 0 || rawY < 0 || rawX > vw || rawY > vh)) {
        // 越界且不要求钳制：不送进页面（普通悬停移出画面后不该继续产生页面内事件）
        return null;
    }

    return {
        x: Math.max(0, Math.min(vw, rawX)),
        y: Math.max(0, Math.min(vh, rawY))
    };
}

function sendResize(state) {
    const rect = stageRect(state);
    if (!rect) return;

    // 宽高取面板实际 **CSS 像素**：这一层是「网页按多大窗口排版」，
    // 必须用 CSS 像素，否则网页会以为自己在 2 倍宽的屏幕上（排版变稀疏）。
    //
    // 用 stage 尺寸（不是 canvas 尺寸）：canvas 的 CSS 尺寸是按帧比例算出来的，
    // 用它推视口会形成「帧 → 画布 → 视口 → 帧」的自激循环。
    //
    // 注意服务端会把宽高各自钳制到 [320,4096]×[240,4096]。
    // 这里的上下限必须与 BrowserOptions.EffectiveWidth/Height 完全一致：
    // 前端钳得更小会让面板被「人为缩小」；钳得更大则服务端再缩一次，
    // 而坐标换算用的 viewportWidth 与实际帧尺寸不符 → 点击系统性偏移。
    const width = Math.round(Math.max(320, Math.min(4096, rect.width)));
    const height = Math.round(Math.max(240, Math.min(4096, rect.height)));

    // ⚠ 只发面板尺寸 + dpr，**不乘任何东西**。
    // 超采样（视口 = 面板 × dpr）是服务端的决定，前端不得预先乘：
    // 一旦两端都乘，视口会被平方放大。
    //
    // 也不在这里写 state.viewportWidth/Height：坐标权威只来自服务端 state
    // （超采样下它 ≠ 面板尺寸）。本地先写会造成「点击量程」与画布显示两套值。
    //
    // 清晰度单独一层：告诉服务端「按几倍像素出图」。
    // 高 DPI 屏上 CSS 554px 占 1108 物理像素，若 screencast 仍按 554 编码，
    // 浏览器会把帧放大 2 倍显示 → 文字发虚（用户报的「分辨率很低」）。
    // 夹在 1–2 与服务端 BrowserPageHandle.NormalizePixelRatio 一致：
    // 两端夹的范围不同会让判重永久失配（每条 resize 都重建 screencast）。
    const dpr = Math.min(Math.max(window.devicePixelRatio || 1, 1), 2);

    // 拖动分隔条时 ResizeObserver 会连续触发（每秒数十次）。每一次 resize 在服务端都意味着
    // SetViewport + 重建 CDP screencast（stop/create/detach/start 四个往返），
    // 画面会闪、CPU 飙高，还会与 agent 的工具调用抢页锁。
    // 因此去抖 + 「尺寸没变就不发」。
    if (width === state.lastSentResizeWidth
        && height === state.lastSentResizeHeight
        && dpr === state.lastSentDpr)
        return;

    if (state.resizeTimer) clearTimeout(state.resizeTimer);

    state.resizeTimer = setTimeout(() => {
        state.resizeTimer = null;
        state.lastSentResizeWidth = width;
        state.lastSentResizeHeight = height;
        state.lastSentDpr = dpr;
        sendRaw(state, { type: 'resize', width, height, pixelRatio: dpr });
        // 视口变了：新尺寸下的画面要重新等一次「静止」才补静帧
        markActivity(state);
    }, DEFAULTS.resizeDebounceMs);

    // 面板尺寸变了，画面的 contain 结果也要跟着变（帧比例不变，可用空间变了）。
    // 不等去抖：resize 会连续触发，这里立刻适配手感更好，且计算只是两次 getBoundingClientRect。
    applyCanvasFit(state);
}

// ── 对外接口（Blazor 调用）────────────────────────────────────────────

export function attach(host, canvasElement, stageElement, dotnetRef, streamUrl) {
    const state = getState(host);
    state.canvas = canvasElement;
    state.stage = stageElement;
    state.ctx = canvasElement.getContext('2d');
    state.dotnet = dotnetRef;
    state.url = streamUrl;
    state.closedByUser = false;
    // 重新挂载 = 重新连接：代次与序号基准都必须归零，
    // 否则新连接的第一帧会被上一连接的序号挡掉（画面停在旧帧）
    state.epoch = 0;
    state.lastFlowSeq = 0;
    state.lastDrawnSeq = 0;
    state.lastFrameSeq = 0;
    state.viewportWidth = DEFAULTS.viewportWidth;
    state.viewportHeight = DEFAULTS.viewportHeight;
    state.frameScale = 1;
    state.mode = 'normal';
    if (state.stillTimer !== null) {
        clearTimeout(state.stillTimer);
        state.stillTimer = null;
    }
    clearHiresPending(state);
    state.stillCoveredRev = 0;
    state.opened = false;
    state.closedReason = null;
    state.closedReasonKind = null;
    state.backoffMs = DEFAULTS.minBackoffMs;

    // 观察 **stage** 而不是 canvas：canvas 的 CSS 尺寸是被 stage 约束的，
    // 观察它会因为「画布尺寸随帧变化」引发自激（帧→画布→resize→帧…）。
    if (!state.resizeObserver && typeof ResizeObserver !== 'undefined') {
        state.resizeObserver = new ResizeObserver(() => sendResize(state));
    }
    if (state.resizeObserver && state.stage) {
        state.resizeObserver.observe(state.stage);
    }

    connect(state);
}

export function detach(host) {
    const state = states.get(host);
    if (!state) return;

    state.closedByUser = true;

    if (state.reconnectTimer) {
        clearTimeout(state.reconnectTimer);
        state.reconnectTimer = null;
    }

    state.resizeObserver?.disconnect();
    state.resizeObserver = null;

    // 静帧的去抖与在途计时都要停：detach 之后没人收帧，
    // 让定时器继续跑会在新连接上误发一次 hires（rev 属于旧连接）
    if (state.stillTimer !== null) {
        clearTimeout(state.stillTimer);
        state.stillTimer = null;
    }
    clearHiresPending(state);
    if (state.resizeTimer !== null) {
        clearTimeout(state.resizeTimer);
        state.resizeTimer = null;
    }

    // 取消未提交的移动帧：否则 detach 后还会往已关闭的 socket 发一条
    if (state.moveRaf !== 0) {
        cancelAnimationFrame(state.moveRaf);
        state.moveRaf = 0;
    }
    state.pendingMove = null;
    state.dragging = false;

    // 清掉 JS 设置的行内尺寸：否则同一 DOM 节点被重新 attach 时，
    // 会先用旧比例的尺寸渲染一帧（表现为「打开瞬间画面比例是错的」）。
    if (state.canvas) {
        state.canvas.style.width = '';
        state.canvas.style.height = '';
    }

    if (state.socket) {
        try { state.socket.close(); } catch { }
        state.socket = null;
    }

    states.delete(host);
}

/// 把「人敲进地址栏的东西」规范成绝对地址。
///
/// 浏览器地址栏的惯例是「输入 baidu.com 就打开它」，而服务端的准入策略只接受
/// `http(s)://` 开头的绝对地址。若不在这一层补全，用户输入裸域名会被直接拒绝
/// （实测：`bing.com` / `www.bing.com` 都返回「不是合法的绝对地址」），
/// 而那条提示显示在状态栏一角、很容易被忽略，表现就是「输入网址没反应」。
///
/// 只补 `https://`：现代站点默认 TLS，且裸 http 会被很多站点重定向到 https。
function normalizeUrl(raw) {
    const text = (raw || '').trim();
    if (text.length === 0) return text;

    // 已经有协议（含 about:/data:/file: 等）就原样交给服务端判定，
    // 不在前端做协议白名单——那是 BrowserUrlPolicy 的职责，只有一处。
    if (/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(text)) return text;

    // 本地文件路径 / 明显不是域名的输入：交给服务端拒绝并给出原因
    if (/[\\\s]/.test(text)) return text;

    // localhost:3000、127.0.0.1:8080、example.com/path?q=1 都按 https 补全。
    // 端口形式（host:port）也要能识别，否则会被误判成协议。
    if (/^[^\s/]+(:\d+)?([/?#].*)?$/.test(text)) return 'https://' + text;

    return text;
}

export function navigate(host, url) {
    const state = states.get(host);
    if (!state) return false;
    return sendRaw(state, { type: 'navigate', url: normalizeUrl(url) });
}

export function reload(host) {
    const state = states.get(host);
    if (!state) return false;
    return sendRaw(state, { type: 'reload' });
}

export function goBack(host) {
    const state = states.get(host);
    if (!state) return false;
    return sendRaw(state, { type: 'history', direction: 'back' });
}

export function goForward(host) {
    const state = states.get(host);
    if (!state) return false;
    return sendRaw(state, { type: 'history', direction: 'forward' });
}

/// 接管：请求服务端中断智能体当前轮并告知它。
///
/// 服务端回一条 `{type:'takeover', interrupted, actionCount}`，前端据此解除遮罩。
/// 不在这里直接改本地状态：必须等服务端确认，否则「点了接管但中断失败」时
/// 用户会以为已获得控制权，实际 agent 还在操作 —— 那种假象比不响应更糟。
export function takeover(host) {
    const state = states.get(host);
    if (!state) return false;
    return sendRaw(state, { type: 'takeover' });
}

// ── 输入采集 ─────────────────────────────────────────────────────────

export function bindInput(host) {
    const state = states.get(host);
    if (!state || !state.canvas || state.inputBound) return;
    state.inputBound = true;

    const canvas = state.canvas;

    canvas.addEventListener('mousedown', (e) => {
        e.preventDefault();
        canvas.focus();
        state.dragging = true;
        const point = toViewportPoint(state, e, true);
        if (!point) return;
        // 按下时立刻冲掉待发的移动帧：否则 rAF 里的旧位置会在 down 之后才发出，
        // 页面收到「先按下→再移动回按下点」的错序序列，某些滑块组件会因此立刻判定失败。
        CancelPendingMove(state);
        sendRaw(state, {
            type: 'mouse', event: 'down', x: point.x, y: point.y,
            button: normalizeButton(e.button)
        });
        // 人工操作会改变画面：重新开始静帧去抖（停下后要一张清晰图）
        markActivity(state);
    });

    canvas.addEventListener('mouseup', (e) => {
        e.preventDefault();
        state.dragging = false;
        const point = toViewportPoint(state, e, true);
        if (!point) return;
        // 同理：抬起前先把最后一次移动发出去，否则「up」会先于最后一段 move 到达，
        // 滑块停在旧位置（用户看到的就是「拖到一半卡住」）。
        FlushPendingMove(state);
        sendRaw(state, {
            type: 'mouse', event: 'up', x: point.x, y: point.y,
            button: normalizeButton(e.button)
        });
        markActivity(state);
    });

    // 鼠标离开画布也要收尾：否则拖动中把指针拖出去再松开，
    // 页面会永远停在「按下」状态（滑块卡在拖动中，后续点击全失效）。
    canvas.addEventListener('mouseleave', () => {
        if (!state.dragging) return;
        state.dragging = false;
        sendRaw(state, { type: 'mouse', event: 'up', x: state.lastPoint?.x ?? 0, y: state.lastPoint?.y ?? 0, button: 'left' });
    });

    // 拖动必须**节流到帧率**，不能每个 mousemove 都发。
    //
    // 两个原因（都是实测症状）：
    //   ① 服务端每次 mousemove 是一条 WS 消息 + 一次 CDP 往返；
    //      浏览器每秒能产生上百个 mousemove，拖动时队列积压 → 滑块跟不上手指。
    //   ② 高频状态推送让「智能体正在操作」提示反复闪。
    //
    // 用 rAF 合并：一帧内无论移动多少次，只发最后一个位置。
    // 这正是真实浏览器自己处理指针移动的方式（指针事件本身也是按帧合并的）。
    canvas.addEventListener('mousemove', (e) => {
        // 拖动时允许坐标略微越界（由 toViewportPoint 钳到边界而不是返回 null），
        // 否则指针一离开画面区域就丢事件，滑块立刻停住 —— 这就是「拖不动」的直接原因。
        const point = toViewportPoint(state, e, state.dragging);
        if (!point) return;

        state.lastPoint = point;
        state.pendingMove = point;

        if (state.moveRaf !== 0) return;
        state.moveRaf = requestAnimationFrame(() => {
            state.moveRaf = 0;
            const p = state.pendingMove;
            state.pendingMove = null;
            if (!p) return;
            sendRaw(state, { type: 'mouse', event: 'move', x: p.x, y: p.y });
        });
    });

    // 不 preventDefault 的话页面会同时滚动外层容器，画面与内容错位
    canvas.addEventListener('wheel', (e) => {
        e.preventDefault();
        sendRaw(state, { type: 'wheel', deltaX: e.deltaX, deltaY: e.deltaY });
        // 滚动是「画面在动」的最强信号：停下 200ms 后才值得补一张清晰图
        markActivity(state);
    }, { passive: false });

    canvas.addEventListener('keydown', (e) => {
        if (isBrowserShortcut(e)) return;    // Ctrl+R / F5 等交回浏览器

        e.preventDefault();
        sendRaw(state, {
            type: 'key', event: 'down', key: e.key,
            ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, meta: e.metaKey
        });
    });

    canvas.addEventListener('keyup', (e) => {
        if (isBrowserShortcut(e)) return;

        e.preventDefault();
        sendRaw(state, {
            type: 'key', event: 'up', key: e.key,
            ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, meta: e.metaKey
        });
    });

    // 输入法提交的整段文本（中文等）走 text 通道：逐个 key 无法还原 IME 结果
    canvas.addEventListener('compositionend', (e) => {
        if (!e.data) return;
        sendRaw(state, { type: 'text', text: e.data });
    });

    // 粘贴：把文本发给页面
    canvas.addEventListener('paste', (e) => {
        const text = e.clipboardData?.getData('text');
        if (!text) return;
        e.preventDefault();
        sendRaw(state, { type: 'text', text });
    });
}

function normalizeButton(button) {
    if (button === 1) return 'middle';
    if (button === 2) return 'right';
    return 'left';
}

// 这些组合键属于「操作浏览器本身」，不该被转发给页面
function isBrowserShortcut(event) {
    if (event.key === 'F5' || event.key === 'F11' || event.key === 'F12') return true;
    if ((event.ctrlKey || event.metaKey) && ['r', 'R', 'w', 'W', 't', 'T', 'n', 'N'].includes(event.key)) return true;
    if ((event.ctrlKey || event.metaKey) && event.shiftKey && ['i', 'I', 'j', 'J'].includes(event.key)) return true;
    return false;
}
