"use strict";

// 协议独占 stdout。先留下真正的 write，再把日志出口全部转到 stderr。
const realStdoutWrite = process.stdout.write.bind(process.stdout);

process.stdout.write = function redirectedStdoutWrite(chunk, encoding, callback) {
  return process.stderr.write(chunk, encoding, callback);
};

console.log = console.error.bind(console);
console.info = console.error.bind(console);
console.debug = console.error.bind(console);
console.warn = console.error.bind(console);

const readline = require("readline");
const { createRequire } = require("module");

function errorMessage(err) {
  if (err && typeof err.message === "string" && err.message.length > 0)
    return err.message;
  return String(err);
}

function reply(obj) {
  let payload;
  try {
    payload = JSON.stringify(obj);
  } catch (err) {
    payload = JSON.stringify({
      id: obj && obj.id,
      ok: false,
      error: errorMessage(err),
    });
  }
  realStdoutWrite(payload + "\n");
}

async function handleLine(line) {
  let request;
  try {
    request = JSON.parse(line);
  } catch {
    return;
  }

  if (!request || typeof request !== "object" || typeof request.id !== "string")
    return;

  const id = request.id;
  try {
    if (typeof request.script !== "string" || request.script.length === 0) {
      reply({ id, ok: false, error: "script 不是路径" });
      return;
    }

    const userRequire = createRequire(request.script);
    const resolved = userRequire.resolve(request.script);
    delete userRequire.cache[resolved];
    const loaded = userRequire(request.script);

    if (typeof loaded !== "function") {
      reply({ id, ok: false, error: "module.exports 不是函数" });
      return;
    }

    const value = await loaded(request.event);
    if (value === null || value === undefined) {
      reply({ id, ok: true, continue: true });
      return;
    }

    if (typeof value !== "object") {
      reply({ id, ok: false, error: "返回值不是对象" });
      return;
    }

    reply({
      id,
      ok: true,
      continue: typeof value.continue === "boolean" ? value.continue : true,
      mutable: value.mutable === undefined ? null : value.mutable,
    });
  } catch (err) {
    reply({ id, ok: false, error: errorMessage(err) });
  }
}

const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
let chain = Promise.resolve();

rl.on("line", (line) => {
  chain = chain.then(() => handleLine(line)).catch((err) => {
    try {
      process.stderr.write(errorMessage(err) + "\n");
    } catch {
      // stderr 也写失败时不再碰 stdout。
    }
  });
});

rl.on("close", () => {
  // 先写完已经读入的响应再退出。卡死的用户函数由宿主在 2 秒后 Kill。
  chain.then(() => process.exit(0), () => process.exit(0));
});
