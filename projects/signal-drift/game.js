const canvas = document.getElementById("game");
const ctx = canvas.getContext("2d");

const scoreEl = document.getElementById("score");
const comboEl = document.getElementById("combo");
const livesEl = document.getElementById("lives");
const overlay = document.getElementById("overlay");
const overlayText = document.getElementById("overlayText");
const startButton = document.getElementById("startButton");
const pauseButton = document.getElementById("pauseButton");
const restartButton = document.getElementById("restartButton");
const touchPad = document.getElementById("touchPad");
const stick = document.getElementById("stick");

const world = { width: 960, height: 540 };
const keys = new Set();
const pointer = { active: false, x: 0, y: 0 };

let state;
let rafId = 0;
let lastTime = 0;

function createState() {
  return {
    mode: "ready",
    time: 0,
    spawnShard: 0.35,
    spawnGate: 0.9,
    score: 0,
    combo: 1,
    lives: 3,
    shake: 0,
    player: { x: 210, y: 270, vx: 0, vy: 0, radius: 17, invulnerable: 0 },
    shards: [],
    gates: [],
    sparks: [],
    stars: Array.from({ length: 110 }, () => ({
      x: Math.random() * world.width,
      y: Math.random() * world.height,
      z: 0.45 + Math.random() * 1.2,
      hue: Math.random() > 0.72 ? "cyan" : "white",
    })),
  };
}

function resizeCanvas() {
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  canvas.width = Math.floor(world.width * ratio);
  canvas.height = Math.floor(world.height * ratio);
  ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
}

function startGame() {
  state = createState();
  state.mode = "playing";
  overlay.classList.add("hidden");
  updateHud();
  lastTime = performance.now();
  cancelAnimationFrame(rafId);
  rafId = requestAnimationFrame(loop);
}

function setOverlay(title, message, buttonLabel) {
  overlay.querySelector("h1").textContent = title;
  overlayText.textContent = message;
  startButton.textContent = buttonLabel;
  overlay.classList.remove("hidden");
}

function togglePause() {
  if (state.mode === "playing") {
    state.mode = "paused";
    setOverlay("已暂停", "漂移轨迹保持稳定。", "继续");
    pauseButton.textContent = "继续";
    return;
  }

  if (state.mode === "paused") {
    state.mode = "playing";
    overlay.classList.add("hidden");
    pauseButton.textContent = "暂停";
    lastTime = performance.now();
    rafId = requestAnimationFrame(loop);
  }
}

function spawnShard() {
  state.shards.push({
    x: world.width + 24,
    y: 82 + Math.random() * (world.height - 164),
    vx: -185 - state.time * 4 - Math.random() * 80,
    radius: 12,
    spin: Math.random() * Math.PI,
  });
}

function spawnGate() {
  const gap = Math.max(118, 190 - state.time * 1.7);
  const center = 96 + Math.random() * (world.height - 192);
  const thickness = 26 + Math.random() * 16;
  const speed = -215 - state.time * 5;

  state.gates.push({ x: world.width + 34, y: 0, width: thickness, height: center - gap / 2, vx: speed });
  state.gates.push({
    x: world.width + 34,
    y: center + gap / 2,
    width: thickness,
    height: world.height - center - gap / 2,
    vx: speed,
  });
}

function addSparks(x, y, color, count) {
  for (let i = 0; i < count; i += 1) {
    const angle = Math.random() * Math.PI * 2;
    const speed = 65 + Math.random() * 190;
    state.sparks.push({
      x,
      y,
      vx: Math.cos(angle) * speed,
      vy: Math.sin(angle) * speed,
      life: 0.35 + Math.random() * 0.35,
      maxLife: 0.7,
      color,
    });
  }
}

function getInputVector() {
  let x = 0;
  let y = 0;

  if (keys.has("arrowleft") || keys.has("a")) x -= 1;
  if (keys.has("arrowright") || keys.has("d")) x += 1;
  if (keys.has("arrowup") || keys.has("w")) y -= 1;
  if (keys.has("arrowdown") || keys.has("s")) y += 1;
  if (pointer.active) {
    x += pointer.x;
    y += pointer.y;
  }

  const length = Math.hypot(x, y) || 1;
  return { x: x / length, y: y / length };
}

function update(dt) {
  if (state.mode !== "playing") return;

  state.time += dt;
  state.spawnShard -= dt;
  state.spawnGate -= dt;
  state.shake = Math.max(0, state.shake - dt * 22);
  state.player.invulnerable = Math.max(0, state.player.invulnerable - dt);

  if (state.spawnShard <= 0) {
    spawnShard();
    state.spawnShard = Math.max(0.22, 0.62 - state.time * 0.006);
  }

  if (state.spawnGate <= 0) {
    spawnGate();
    state.spawnGate = Math.max(0.86, 1.45 - state.time * 0.01);
  }

  const input = getInputVector();
  const player = state.player;
  const thrust = 980;
  const drag = Math.pow(0.003, dt);
  player.vx = (player.vx + input.x * thrust * dt) * drag;
  player.vy = (player.vy + input.y * thrust * dt) * drag;
  player.x = Math.max(34, Math.min(world.width - 34, player.x + player.vx * dt));
  player.y = Math.max(38, Math.min(world.height - 38, player.y + player.vy * dt));

  for (const star of state.stars) {
    star.x -= (45 + state.time * 1.5) * star.z * dt;
    if (star.x < -8) {
      star.x = world.width + 8;
      star.y = Math.random() * world.height;
    }
  }

  for (const shard of state.shards) {
    shard.x += shard.vx * dt;
    shard.spin += dt * 5;
  }

  for (const gate of state.gates) {
    gate.x += gate.vx * dt;
  }

  for (const spark of state.sparks) {
    spark.x += spark.vx * dt;
    spark.y += spark.vy * dt;
    spark.vx *= Math.pow(0.03, dt);
    spark.vy *= Math.pow(0.03, dt);
    spark.life -= dt;
  }

  collectAndCollide();
  state.shards = state.shards.filter((shard) => shard.x > -40);
  state.gates = state.gates.filter((gate) => gate.x + gate.width > -20);
  state.sparks = state.sparks.filter((spark) => spark.life > 0);
  updateHud();
}

function collectAndCollide() {
  const player = state.player;

  state.shards = state.shards.filter((shard) => {
    if (Math.hypot(player.x - shard.x, player.y - shard.y) < player.radius + shard.radius) {
      state.score += 10 * state.combo;
      state.combo = Math.min(9, state.combo + 1);
      addSparks(shard.x, shard.y, "#b8ff63", 12);
      return false;
    }
    return true;
  });

  if (player.invulnerable > 0) return;

  for (const gate of state.gates) {
    const nearestX = Math.max(gate.x, Math.min(player.x, gate.x + gate.width));
    const nearestY = Math.max(gate.y, Math.min(player.y, gate.y + gate.height));
    if (Math.hypot(player.x - nearestX, player.y - nearestY) < player.radius) {
      state.lives -= 1;
      state.combo = 1;
      state.shake = 9;
      player.invulnerable = 1.1;
      player.vx = -210;
      addSparks(player.x, player.y, "#ff4f69", 24);

      if (state.lives <= 0) {
        state.mode = "over";
        setOverlay("游戏结束", `最终得分 ${state.score}`, "再玩一次");
        pauseButton.textContent = "暂停";
      }
      break;
    }
  }
}

function draw() {
  const shakeX = state.shake ? (Math.random() - 0.5) * state.shake : 0;
  const shakeY = state.shake ? (Math.random() - 0.5) * state.shake : 0;

  ctx.save();
  ctx.clearRect(0, 0, world.width, world.height);
  ctx.translate(shakeX, shakeY);

  drawBackdrop();
  for (const shard of state.shards) drawShard(shard);
  for (const gate of state.gates) drawGate(gate);
  for (const spark of state.sparks) drawSpark(spark);
  drawPlayer(state.player);

  ctx.restore();
}

function drawBackdrop() {
  const gradient = ctx.createLinearGradient(0, 0, world.width, world.height);
  gradient.addColorStop(0, "#071018");
  gradient.addColorStop(0.52, "#0d1b22");
  gradient.addColorStop(1, "#1a151c");
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, world.width, world.height);

  for (const star of state.stars) {
    ctx.globalAlpha = 0.4 + star.z * 0.28;
    ctx.fillStyle = star.hue === "cyan" ? "#3de8ff" : "#f7fbff";
    ctx.fillRect(star.x, star.y, 1.5 * star.z, 1.5 * star.z);
  }
  ctx.globalAlpha = 1;

  ctx.strokeStyle = "rgba(255, 255, 255, 0.045)";
  ctx.lineWidth = 1;
  for (let x = (state.time * -34) % 80; x < world.width; x += 80) {
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x - 140, world.height);
    ctx.stroke();
  }
}

function drawPlayer(player) {
  const blink = player.invulnerable > 0 && Math.floor(player.invulnerable * 12) % 2 === 0;
  if (blink) return;

  ctx.save();
  ctx.translate(player.x, player.y);
  ctx.rotate(player.vy * 0.002);

  ctx.shadowColor = "#3de8ff";
  ctx.shadowBlur = 18;
  ctx.fillStyle = "#3de8ff";
  ctx.beginPath();
  ctx.moveTo(24, 0);
  ctx.lineTo(-16, -15);
  ctx.lineTo(-9, 0);
  ctx.lineTo(-16, 15);
  ctx.closePath();
  ctx.fill();

  ctx.shadowColor = "#ffd166";
  ctx.fillStyle = "#ffd166";
  ctx.beginPath();
  ctx.moveTo(-16, -8);
  ctx.lineTo(-31 - Math.random() * 7, 0);
  ctx.lineTo(-16, 8);
  ctx.closePath();
  ctx.fill();

  ctx.shadowBlur = 0;
  ctx.fillStyle = "#071018";
  ctx.beginPath();
  ctx.arc(5, 0, 5, 0, Math.PI * 2);
  ctx.fill();
  ctx.restore();
}

function drawShard(shard) {
  ctx.save();
  ctx.translate(shard.x, shard.y);
  ctx.rotate(shard.spin);
  ctx.shadowColor = "#b8ff63";
  ctx.shadowBlur = 18;
  ctx.fillStyle = "#b8ff63";
  ctx.beginPath();
  ctx.moveTo(0, -15);
  ctx.lineTo(12, 0);
  ctx.lineTo(0, 15);
  ctx.lineTo(-12, 0);
  ctx.closePath();
  ctx.fill();
  ctx.restore();
}

function drawGate(gate) {
  const glow = ctx.createLinearGradient(gate.x, gate.y, gate.x + gate.width, gate.y);
  glow.addColorStop(0, "rgba(255, 79, 105, 0.34)");
  glow.addColorStop(0.5, "rgba(255, 79, 105, 0.92)");
  glow.addColorStop(1, "rgba(255, 209, 102, 0.44)");
  ctx.fillStyle = glow;
  ctx.shadowColor = "#ff4f69";
  ctx.shadowBlur = 18;
  ctx.fillRect(gate.x, gate.y, gate.width, gate.height);
  ctx.shadowBlur = 0;
}

function drawSpark(spark) {
  ctx.globalAlpha = Math.max(0, spark.life / spark.maxLife);
  ctx.fillStyle = spark.color;
  ctx.beginPath();
  ctx.arc(spark.x, spark.y, 2.5, 0, Math.PI * 2);
  ctx.fill();
  ctx.globalAlpha = 1;
}

function updateHud() {
  scoreEl.textContent = String(state.score);
  comboEl.textContent = `x${state.combo}`;
  livesEl.textContent = String(state.lives);
}

function loop(time) {
  const dt = Math.min(0.033, (time - lastTime) / 1000 || 0);
  lastTime = time;
  update(dt);
  draw();
  if (state.mode === "playing") rafId = requestAnimationFrame(loop);
}

function setPointerFromEvent(event) {
  const rect = touchPad.getBoundingClientRect();
  const centerX = rect.left + rect.width / 2;
  const centerY = rect.top + rect.height / 2;
  const dx = event.clientX - centerX;
  const dy = event.clientY - centerY;
  const distance = Math.min(38, Math.hypot(dx, dy));
  const angle = Math.atan2(dy, dx);
  pointer.x = Math.cos(angle) * (distance / 38);
  pointer.y = Math.sin(angle) * (distance / 38);
  stick.style.transform = `translate(${Math.cos(angle) * distance}px, ${Math.sin(angle) * distance}px)`;
}

window.addEventListener("resize", resizeCanvas);
window.addEventListener("keydown", (event) => {
  keys.add(event.key.toLowerCase());
  if (event.key === " " || event.key.toLowerCase() === "p") {
    event.preventDefault();
    togglePause();
  }
});
window.addEventListener("keyup", (event) => keys.delete(event.key.toLowerCase()));

touchPad.addEventListener("pointerdown", (event) => {
  pointer.active = true;
  touchPad.setPointerCapture(event.pointerId);
  setPointerFromEvent(event);
});
touchPad.addEventListener("pointermove", (event) => {
  if (pointer.active) setPointerFromEvent(event);
});
touchPad.addEventListener("pointerup", () => {
  pointer.active = false;
  pointer.x = 0;
  pointer.y = 0;
  stick.style.transform = "translate(0, 0)";
});

startButton.addEventListener("click", () => {
  if (state.mode === "paused") {
    togglePause();
    return;
  }
  startGame();
});
pauseButton.addEventListener("click", togglePause);
restartButton.addEventListener("click", startGame);

resizeCanvas();
state = createState();
updateHud();
draw();
