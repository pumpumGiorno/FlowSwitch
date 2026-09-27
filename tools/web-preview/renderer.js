// Executes FlowSwitch display lists (produced by tools/ScenePreview from the real Core engine)
// with the real HLSL shaders cross-compiled to GLSL ES by build-shaders.sh.
import { shaders } from './generated/shaders.js';
import { paintDesktop, paintApp, paintIcon, paintText, appKind } from './mock.js';

const SHADER = ['PSBackdrop', 'PSCard', 'PSSprite', 'PSGlow', 'PSOrbit', 'PSPill'];
const TEX = { None: 0, Preview: 1, Icon: 2, Text: 3 };

export class PreviewRenderer {
  constructor(canvas, scene) {
    this.scene = scene;
    canvas.width = scene.width;
    canvas.height = scene.height;
    const gl = canvas.getContext('webgl2', { premultipliedAlpha: true, alpha: false, antialias: false, preserveDrawingBuffer: true });
    if (!gl) throw new Error('WebGL2 unavailable');
    this.gl = gl;
    this.programs = {};
    for (const ps of SHADER) {
      const vs = ps === 'PSBackdrop' ? shaders.VSFullscreen : shaders.VSQuad;
      this.programs[ps] = this.link(vs, shaders[ps], ps);
    }
    this.blit = this.link(
      '#version 300 es\nout vec2 uv;\nvoid main(){ vec2 p = vec2(float((gl_VertexID<<1)&2), float(gl_VertexID&2)); uv = p; gl_Position = vec4(p.x*2.0-1.0, 1.0-p.y*2.0, 0.0, 1.0); }',
      '#version 300 es\nprecision highp float; in vec2 uv; uniform sampler2D tex; out vec4 o; void main(){ o = vec4(texture(tex, uv).rgb, 1.0); }', 'blit');
    this.frameUbo = gl.createBuffer();
    this.drawUbo = gl.createBuffer();
    gl.bindBuffer(gl.UNIFORM_BUFFER, this.frameUbo);
    gl.bufferData(gl.UNIFORM_BUFFER, 64, gl.DYNAMIC_DRAW);
    gl.bindBuffer(gl.UNIFORM_BUFFER, this.drawUbo);
    gl.bufferData(gl.UNIFORM_BUFFER, 192, gl.DYNAMIC_DRAW);
    gl.bindBufferBase(gl.UNIFORM_BUFFER, 0, this.frameUbo);
    gl.bindBufferBase(gl.UNIFORM_BUFFER, 1, this.drawUbo);
    this.vao = gl.createVertexArray();

    this.textures = new Map();
    this.textCache = new Map();
    this.buildScene();
  }

  link(vsSrc, fsSrc, name) {
    const gl = this.gl;
    const compile = (type, src) => {
      const sh = gl.createShader(type);
      gl.shaderSource(sh, src);
      gl.compileShader(sh);
      if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) throw new Error(`${name}: ${gl.getShaderInfoLog(sh)}`);
      return sh;
    };
    const p = gl.createProgram();
    gl.attachShader(p, compile(gl.VERTEX_SHADER, vsSrc));
    gl.attachShader(p, compile(gl.FRAGMENT_SHADER, fsSrc));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(`${name}: ${gl.getProgramInfoLog(p)}`);
    const fb = gl.getUniformBlockIndex(p, 'FrameCB');
    if (fb !== gl.INVALID_INDEX) gl.uniformBlockBinding(p, fb, 0);
    const db = gl.getUniformBlockIndex(p, 'DrawCB');
    if (db !== gl.INVALID_INDEX) gl.uniformBlockBinding(p, db, 1);
    gl.useProgram(p);
    const bt = gl.getUniformLocation(p, 'SPIRV_Cross_CombinedgBackdropTexgLinear');
    if (bt) gl.uniform1i(bt, 0);
    const tt = gl.getUniformLocation(p, 'SPIRV_Cross_CombinedgTexgLinear');
    if (tt) gl.uniform1i(tt, 1);
    const blitTex = gl.getUniformLocation(p, 'tex');
    if (blitTex) gl.uniform1i(blitTex, 0);
    return p;
  }

  upload(source, { mips = true, premultiply = true } = {}) {
    const gl = this.gl;
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, premultiply);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, mips ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR);
    if (mips) gl.generateMipmap(gl.TEXTURE_2D);
    return { tex: t, w: source.width, h: source.height };
  }

  buildScene() {
    const { width, height } = this.scene;
    this.desktop = paintDesktop(width, height);
    this.desktopTex = this.upload(this.desktop, { mips: false });

    // Backdrop pyramid: level 0 = half resolution, each further level halves and blurs (like the
    // 13-tap downsample chain the D3D renderer builds).
    const gl = this.gl;
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
    let w = Math.round(width / 2), h = Math.round(height / 2);
    let prev = this.desktop;
    let level = 0;
    while (level < 8) {
      const c = document.createElement('canvas');
      c.width = w; c.height = h;
      const ctx = c.getContext('2d');
      ctx.filter = level === 0 ? 'blur(0.6px)' : 'blur(1.4px)';
      ctx.drawImage(prev, -2, -2, w + 4, h + 4);
      gl.texImage2D(gl.TEXTURE_2D, level, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, c);
      prev = c;
      if (w === 1 && h === 1) break;
      w = Math.max(1, w >> 1); h = Math.max(1, h >> 1);
      level++;
    }
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAX_LEVEL, level);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    this.backdrop = t;

    // Window previews and icons.
    this.scene.windows.forEach((w, i) => {
      const kind = appKind(w.exe);
      const pw = 1280, ph = Math.round(1280 / Math.max(0.5, w.aspect));
      this.textures.set(`p:${w.handle}`, this.upload(paintApp(kind, pw, ph, i + 1, w.title)));
      if (!this.textures.has(`i:${w.appId}`)) this.textures.set(`i:${w.appId}`, this.upload(paintIcon(kind, 256)));
    });
  }

  texture(cmd) {
    switch (cmd.tk) {
      case TEX.Preview: return this.textures.get(`p:${cmd.th}`);
      case TEX.Icon: return this.textures.get(`i:${cmd.key}`);
      case TEX.Text: {
        const s = cmd.text;
        const key = `${s.text}|${s.size}|${s.weight}|${s.family}|${s.maxWidth}`;
        let t = this.textCache.get(key);
        if (!t) { t = this.upload(paintText(s)); this.textCache.set(key, t); }
        return t;
      }
      default: return null;
    }
  }

  render(frameIndex) {
    const gl = this.gl;
    const frame = this.scene.frames[Math.min(frameIndex, this.scene.frames.length - 1)];
    gl.viewport(0, 0, this.scene.width, this.scene.height);
    gl.bindVertexArray(this.vao);

    // The real desktop under the overlay.
    gl.disable(gl.BLEND);
    gl.useProgram(this.blit);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.desktopTex.tex);
    gl.drawArrays(gl.TRIANGLES, 0, 3);

    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    gl.bindBuffer(gl.UNIFORM_BUFFER, this.frameUbo);
    gl.bufferSubData(gl.UNIFORM_BUFFER, 0, new Float32Array(frame.frame));
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.backdrop);

    for (const cmd of frame.cmds) {
      const p = new Float32Array(cmd.p);
      const tex = this.texture(cmd);
      if (cmd.s === 2 && p[30] === 1) {
        // TextPlacement.Resolve (FlowSwitch.Core/Scene/TextPlacement.cs).
        const k = p[31] <= 0 ? 1 : p[31];
        const sw = tex.w * k, sh = tex.h * k;
        const x0 = p[16] - p[18] * sw, y0 = p[17] - p[19] * sh;
        p.set([sw / 2, sh / 2, 0, 0], 4);
        p.set([x0 + sw / 2, y0 + sh / 2, 0, 0], 12);
        p.set([x0, y0, x0 + sw, y0 + sh], 16);
        p.set([0, 0, 1, 1], 20);
        p[30] = 0;
      }
      gl.useProgram(this.programs[SHADER[cmd.s]]);
      gl.bindBuffer(gl.UNIFORM_BUFFER, this.drawUbo);
      gl.bufferSubData(gl.UNIFORM_BUFFER, 0, p);
      gl.activeTexture(gl.TEXTURE1);
      gl.bindTexture(gl.TEXTURE_2D, tex ? tex.tex : null);
      if (cmd.s === 0) gl.drawArrays(gl.TRIANGLES, 0, 3);
      else gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    }
    return frame.cmds.length;
  }
}
