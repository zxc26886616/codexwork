const crypto = require("node:crypto");

const DH_PRIME = 0xffffffffffffffc5n;
const DH_GENERATOR = 5n;

const MD5_K = [
  0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee,
  0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
  0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
  0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
  0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa,
  0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
  0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed,
  0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
  0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
  0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
  0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05,
  0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
  0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039,
  0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
  0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
  0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
];

const MD5_R = [
  7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
  5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
  4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
  6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
];

function requireEightBytes(value, name) {
  const buffer = Buffer.from(value || []);
  if (buffer.length !== 8) {
    throw new Error(`${name} must be exactly 8 bytes`);
  }
  return buffer;
}

function readUInt64LE(buffer) {
  return requireEightBytes(buffer, "uint64").readBigUInt64LE(0);
}

function writeUInt64LE(value) {
  const buffer = Buffer.alloc(8);
  buffer.writeBigUInt64LE(value, 0);
  return buffer;
}

function modPow(base, exponent, modulus) {
  if (exponent === 0n) return 1n;
  let result = 1n;
  let current = base % modulus;
  let power = exponent;
  while (power > 0n) {
    if ((power & 1n) === 1n) {
      result = (result * current) % modulus;
    }
    current = (current * current) % modulus;
    power >>= 1n;
  }
  return result;
}

function dhExchange(privateKey) {
  const exponent = readUInt64LE(requireEightBytes(privateKey, "privateKey"));
  if (exponent === 0n) throw new Error("privateKey cannot be zero");
  return writeUInt64LE(modPow(DH_GENERATOR, exponent, DH_PRIME));
}

function dhSecret(serverPublicKey, clientPrivateKey) {
  const base = readUInt64LE(requireEightBytes(serverPublicKey, "serverPublicKey"));
  const exponent = readUInt64LE(requireEightBytes(clientPrivateKey, "clientPrivateKey"));
  if (base === 0n || exponent === 0n) throw new Error("DH values cannot be zero");
  return writeUInt64LE(modPow(base, exponent, DH_PRIME));
}

function rotateLeft(value, bits) {
  return ((value << bits) | (value >>> (32 - bits))) >>> 0;
}

function digestMd5WithoutPadding(words) {
  let a = 0x67452301;
  let b = 0xefcdab89;
  let c = 0x98badcfe;
  let d = 0x10325476;

  for (let i = 0; i < 64; i += 1) {
    let f;
    let g;
    if (i < 16) {
      f = ((b & c) | (~b & d)) >>> 0;
      g = i;
    } else if (i < 32) {
      f = ((d & b) | (~d & c)) >>> 0;
      g = (5 * i + 1) % 16;
    } else if (i < 48) {
      f = (b ^ c ^ d) >>> 0;
      g = (3 * i + 5) % 16;
    } else {
      f = (c ^ (b | ~d)) >>> 0;
      g = (7 * i) % 16;
    }

    const previousD = d;
    d = c;
    c = b;
    const sum = (a + f + MD5_K[i] + words[g]) >>> 0;
    b = (b + rotateLeft(sum, MD5_R[i])) >>> 0;
    a = previousD;
  }

  return [a >>> 0, b >>> 0, c >>> 0, d >>> 0];
}

function hmac64(left, right) {
  const x = requireEightBytes(left, "hmac left");
  const y = requireEightBytes(right, "hmac right");
  const x0 = x.readUInt32LE(0);
  const x1 = x.readUInt32LE(4);
  const y0 = y.readUInt32LE(0);
  const y1 = y.readUInt32LE(4);
  const words = new Array(16);
  for (let i = 0; i < 16; i += 4) {
    words[i] = x1;
    words[i + 1] = x0;
    words[i + 2] = y1;
    words[i + 3] = y0;
  }
  const digest = digestMd5WithoutPadding(words);
  const result = Buffer.alloc(8);
  result.writeUInt32LE((digest[2] ^ digest[3]) >>> 0, 0);
  result.writeUInt32LE((digest[0] ^ digest[1]) >>> 0, 4);
  return result;
}

function hashKey(value) {
  const bytes = Buffer.isBuffer(value) ? value : Buffer.from(String(value), "utf8");
  let djbHash = 5381;
  let jsHash = 1315423911;
  for (const byte of bytes) {
    djbHash = (djbHash + ((djbHash << 5) >>> 0) + byte) >>> 0;
    jsHash = (jsHash ^ (((jsHash << 5) >>> 0) + byte + (jsHash >>> 2))) >>> 0;
  }
  const result = Buffer.alloc(8);
  result.writeUInt32LE(djbHash, 0);
  result.writeUInt32LE(jsHash, 4);
  return result;
}

function padIso7816(value) {
  const input = Buffer.from(value);
  const size = (input.length + 8) & ~7;
  const padded = Buffer.alloc(size);
  input.copy(padded);
  padded[input.length] = 0x80;
  return padded;
}

function desEncode(key, value) {
  const desKey = requireEightBytes(key, "DES key");
  // OpenSSL 的 3DES 在 K1=K2=K3 时与单 DES 完全等价。
  const tripleKey = Buffer.concat([desKey, desKey, desKey]);
  const cipher = crypto.createCipheriv("des-ede3-ecb", tripleKey, null);
  cipher.setAutoPadding(false);
  return Buffer.concat([cipher.update(padIso7816(value)), cipher.final()]);
}

function desDecode(key, value) {
  const desKey = requireEightBytes(key, "DES key");
  const encrypted = Buffer.from(value);
  if (encrypted.length === 0 || encrypted.length % 8 !== 0) {
    throw new Error("Invalid DES encrypted length");
  }
  const tripleKey = Buffer.concat([desKey, desKey, desKey]);
  const decipher = crypto.createDecipheriv("des-ede3-ecb", tripleKey, null);
  decipher.setAutoPadding(false);
  const plain = Buffer.concat([decipher.update(encrypted), decipher.final()]);
  let marker = plain.length - 1;
  while (marker >= plain.length - 8 && plain[marker] === 0) marker -= 1;
  if (marker < plain.length - 8 || plain[marker] !== 0x80) {
    throw new Error("Invalid DES ISO7816 padding");
  }
  return plain.subarray(0, marker);
}

function createRandomKey(randomBytes = crypto.randomBytes) {
  const key = Buffer.from(randomBytes(8));
  if (key.every((byte) => byte === 0)) key[0] = 1;
  return key;
}

function buildLoginToken(options) {
  return [
    options.userId,
    options.channelToken,
    options.platform || "3",
    options.language || options.channel || "CH",
    "",
    options.serverNode || "game1",
    options.websocketFlag ?? 1,
    options.sdkParam || "",
  ].join(":");
}

function encryptLoginToken(secret, options) {
  return desEncode(secret, Buffer.from(buildLoginToken(options), "utf8"));
}

function decodeBase64Text(value) {
  return Buffer.from(value, "base64").toString("utf8");
}

function parseAuthResponse(response) {
  const text = String(response || "").replace(/\0/g, "").trim();
  const code = Number.parseInt(text.slice(0, 3), 10);
  if (!Number.isInteger(code)) throw new Error("Invalid gateway auth response code");
  if (code !== 200) {
    const error = new Error(`Gateway authentication failed: ${code}`);
    error.code = code;
    throw error;
  }
  const encodedPayload = text.split(/\s+/, 2)[1];
  if (!encodedPayload) throw new Error("Gateway auth response is missing payload");
  const fields = decodeBase64Text(encodedPayload).split("@");
  if (fields.length < 5) throw new Error("Gateway auth response payload is incomplete");
  const decoded = fields.map(decodeBase64Text);
  return {
    accountId: decoded[0],
    subId: Number.parseInt(decoded[1], 10),
    gatewayHost: decoded[2],
    gatewayPort: Number.parseInt(decoded[3], 10),
    serverIp: decoded[4],
    serverName: decoded[5] || "",
    uid: decoded[6] ? Number.parseInt(decoded[6], 10) : 0,
    sessionKey: decoded[7] || "",
  };
}

function buildRedirectAuth(secret, options) {
  const base = `${Buffer.from(String(options.uid)).toString("base64")}@${Buffer.from(
    options.serverName,
  ).toString("base64")}#${Buffer.from(String(options.subId)).toString("base64")}:${
    options.connectIndex || 1
  }`;
  const signature = hmac64(hashKey(base), secret).toString("base64");
  return `${base}:${signature}`;
}

function framePackage(payload) {
  const body = Buffer.from(payload);
  if (body.length > 0xffff) throw new Error("Gateway packet is too large");
  const packet = Buffer.alloc(body.length + 2);
  packet.writeUInt16BE(body.length, 0);
  body.copy(packet, 2);
  return packet;
}

module.exports = {
  DH_PRIME,
  buildLoginToken,
  buildRedirectAuth,
  createRandomKey,
  desDecode,
  desEncode,
  dhExchange,
  dhSecret,
  encryptLoginToken,
  framePackage,
  hashKey,
  hmac64,
  parseAuthResponse,
};
