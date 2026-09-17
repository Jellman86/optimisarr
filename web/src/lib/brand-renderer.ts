import { createBrandGeometry } from './brand-geometry'
import { lookAt, mm, mul, norm, ortho } from './brand-math'
import type { BrandMotion } from './brand-motion'
import { vertex, fragment, shadowFragment, receiverFragment } from './brand-shaders'

const SHADOW_SIZE = 1024
const camera = [7.8, 3.2, -4.5]
const view = lookAt(camera, [0, 0, 0])
const projection = mm(ortho(-2.23, 2.23, -2.23, 2.23, 0.1, 30), view)
const lightProjection = ortho(-3.1, 3.1, -3.1, 3.1, 0.1, 20)
const linear = (hex: string) =>
  hex.match(/[0-9a-f]{2}/gi)!.map((pair) => {
    const value = parseInt(pair, 16) / 255
    return value <= 0.04045 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4)
  })
const blend = (a: number[], b: number[], t: number) => a.map((v, i) => v + (b[i] - v) * t)
// These are the accent, accent-strong and panel colours in app.css, in linear space.
const palettes = {
  dark: {
    low: linear('#0e7490'),
    high: linear('#a5f3fc'),
    rim: mul(linear('#22d3ee'), 0.5),
    fill: mul(linear('#22d3ee'), 0.11),
    exposure: 1,
    shadow: 0.27,
  },
  light: {
    low: linear('#164e63'),
    high: blend(linear('#0e7490'), linear('#f8fafd'), 0.16),
    rim: mul(linear('#0e7490'), 0.8),
    fill: mul(linear('#0e7490'), 0.12),
    exposure: 0.88,
    shadow: 0.15,
  },
}

type Mesh = { buffer: WebGLBuffer; count: number }

export function createBrandRenderer() {
  const canvas = document.createElement('canvas')
  const gl = canvas.getContext('webgl', { alpha: true, antialias: true, premultipliedAlpha: false })
  if (!gl) throw new Error('Brand graphics unavailable')
  const cleanup: (() => void)[] = []
  let disposed = false
  let lost = false
  const contextLost = (event: Event) => {
    event.preventDefault()
    lost = true
  }
  canvas.addEventListener('webglcontextlost', contextLost)

  function destroy() {
    if (disposed) return
    disposed = true
    canvas.removeEventListener('webglcontextlost', contextLost)
    for (const release of cleanup.reverse()) release()
    gl!.getExtension('WEBGL_lose_context')?.loseContext()
    canvas.width = canvas.height = 1
  }

  try {
    function program(fragmentSource: string) {
      const handle = gl!.createProgram()
      if (!handle) throw new Error('Cannot allocate brand program')
      cleanup.push(() => gl!.deleteProgram(handle))
      for (const [type, source] of [
        [gl!.VERTEX_SHADER, vertex],
        [gl!.FRAGMENT_SHADER, fragmentSource],
      ] as const) {
        const shader = gl!.createShader(type)
        if (!shader) throw new Error('Cannot allocate brand shader')
        cleanup.push(() => gl!.deleteShader(shader))
        gl!.shaderSource(shader, source)
        gl!.compileShader(shader)
        if (!gl!.getShaderParameter(shader, gl!.COMPILE_STATUS))
          throw new Error(gl!.getShaderInfoLog(shader) ?? 'Brand shader failed')
        gl!.attachShader(handle, shader)
      }
      gl!.linkProgram(handle)
      if (!gl!.getProgramParameter(handle, gl!.LINK_STATUS))
        throw new Error(gl!.getProgramInfoLog(handle) ?? 'Brand program failed')
      const uniforms = new Map<string, WebGLUniformLocation | null>()
      return {
        handle,
        position: gl!.getAttribLocation(handle, 'position'),
        normal: gl!.getAttribLocation(handle, 'normal'),
        uniform(name: string) {
          if (!uniforms.has(name)) uniforms.set(name, gl!.getUniformLocation(handle, name))
          return uniforms.get(name)!
        },
      }
    }
    type Program = ReturnType<typeof program>
    function upload(data: Float32Array): Mesh {
      const buffer = gl!.createBuffer()
      if (!buffer) throw new Error('Cannot allocate brand geometry')
      cleanup.push(() => gl!.deleteBuffer(buffer))
      gl!.bindBuffer(gl!.ARRAY_BUFFER, buffer)
      gl!.bufferData(gl!.ARRAY_BUFFER, data, gl!.STATIC_DRAW)
      return { buffer, count: data.length / 6 }
    }
    const geometry = createBrandGeometry()
    const slices = geometry.slices.map(upload)
    const floor = upload(geometry.floor)
    const material = program(fragment)
    const shadow = program(shadowFragment)
    const receiver = program(receiverFragment)
    const texture = gl.createTexture()
    const framebuffer = gl.createFramebuffer()
    const depth = gl.createRenderbuffer()
    cleanup.push(
      () => gl.deleteTexture(texture),
      () => gl.deleteFramebuffer(framebuffer),
      () => gl.deleteRenderbuffer(depth),
    )
    if (!texture || !framebuffer || !depth) throw new Error('Cannot allocate brand shadow map')
    gl.bindTexture(gl.TEXTURE_2D, texture)
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, SHADOW_SIZE, SHADOW_SIZE, 0, gl.RGBA, gl.UNSIGNED_BYTE, null)
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST)
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST)
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE)
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE)
    gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer)
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0)
    gl.bindRenderbuffer(gl.RENDERBUFFER, depth)
    gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT16, SHADOW_SIZE, SHADOW_SIZE)
    gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth)
    if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE)
      throw new Error('Brand shadow map incomplete')
    gl.bindFramebuffer(gl.FRAMEBUFFER, null)
    gl.enable(gl.DEPTH_TEST)
    gl.enable(gl.CULL_FACE)
    gl.cullFace(gl.BACK)

    function drawMesh(p: Program, mesh: Mesh) {
      gl!.bindBuffer(gl!.ARRAY_BUFFER, mesh.buffer)
      gl!.enableVertexAttribArray(p.position)
      gl!.vertexAttribPointer(p.position, 3, gl!.FLOAT, false, 24, 0)
      if (p.normal >= 0) {
        gl!.enableVertexAttribArray(p.normal)
        gl!.vertexAttribPointer(p.normal, 3, gl!.FLOAT, false, 24, 12)
      }
      gl!.drawArrays(gl!.TRIANGLES, 0, mesh.count)
    }
    function drawObjects(p: Program, matrix: Float32Array, lightMatrix: Float32Array, motion: BrandMotion) {
      gl!.useProgram(p.handle)
      gl!.uniformMatrix4fv(p.uniform('vp'), false, matrix)
      gl!.uniformMatrix4fv(p.uniform('lightVP'), false, lightMatrix)
      slices.forEach((mesh, i) => {
        gl!.uniform1f(p.uniform('angle'), motion.angles[i] % (2 * Math.PI))
        gl!.uniform1f(p.uniform('lift'), i * 0.013 * motion.opening)
        drawMesh(p, mesh)
      })
    }
    return {
      destroy,
      render(motion: BrandMotion, dark: boolean, size: number) {
        if (disposed || lost || gl.isContextLost()) throw new Error('Brand context lost')
        if (canvas.width !== size) canvas.width = canvas.height = size
        const position = [6.1 * Math.cos(motion.lightPhase), 6, 6.1 * Math.sin(motion.lightPhase)]
        const lightMatrix = mm(lightProjection, lookAt(position, [0, 0, 0]))
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer)
        gl.viewport(0, 0, SHADOW_SIZE, SHADOW_SIZE)
        gl.clearColor(1, 1, 1, 1)
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT)
        drawObjects(shadow, lightMatrix, lightMatrix, motion)
        gl.bindFramebuffer(gl.FRAMEBUFFER, null)
        gl.viewport(0, 0, size, size)
        gl.clearColor(0, 0, 0, 0)
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT)
        const palette = palettes[dark ? 'dark' : 'light']
        gl.activeTexture(gl.TEXTURE0)
        gl.bindTexture(gl.TEXTURE_2D, texture)
        gl.useProgram(receiver.handle)
        gl.uniformMatrix4fv(receiver.uniform('vp'), false, projection)
        gl.uniformMatrix4fv(receiver.uniform('lightVP'), false, lightMatrix)
        gl.uniform1f(receiver.uniform('angle'), 0)
        gl.uniform1f(receiver.uniform('lift'), 0)
        gl.uniform1i(receiver.uniform('shadowMap'), 0)
        gl.uniform1f(receiver.uniform('shadowOpacity'), palette.shadow)
        gl.uniform1f(receiver.uniform('outputSize'), size)
        gl.enable(gl.BLEND)
        gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA)
        gl.depthMask(false)
        drawMesh(receiver, floor)
        gl.depthMask(true)
        gl.disable(gl.BLEND)
        gl.useProgram(material.handle)
        gl.uniform3fv(material.uniform('eye'), camera)
        gl.uniform3fv(material.uniform('keyLight'), norm(position))
        gl.uniform1i(material.uniform('shadowMap'), 0)
        gl.uniform1f(material.uniform('exposure'), palette.exposure)
        gl.uniform3fv(material.uniform('baseLow'), palette.low)
        gl.uniform3fv(material.uniform('baseHigh'), palette.high)
        gl.uniform3fv(material.uniform('rimColour'), palette.rim)
        gl.uniform3fv(material.uniform('fillColour'), palette.fill)
        drawObjects(material, projection, lightMatrix, motion)
        return canvas
      },
    }
  } catch (error) {
    destroy()
    throw error
  }
}
