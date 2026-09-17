export const vertex = `
attribute vec3 position;
attribute vec3 normal;
uniform mat4 vp;
uniform mat4 lightVP;
uniform float angle;
uniform float lift;
varying vec3 world;
varying vec3 surfaceNormal;
varying vec4 shadowPosition;
void main() {
  float c = cos(angle), s = sin(angle);
  mat3 rotation = mat3(c,0.,-s, 0.,1.,0., s,0.,c);
  world = rotation * position + vec3(0.,lift,0.);
  surfaceNormal = rotation * normal;
  shadowPosition = lightVP * vec4(world,1.);
  gl_Position = vp * vec4(world,1.);
}`

export const shadowFragment = `
precision highp float;
void main() {
  vec2 encodedDepth = fract(vec2(1.,255.) * gl_FragCoord.z);
  encodedDepth.x -= encodedDepth.y / 255.;
  gl_FragColor = vec4(encodedDepth,0.,1.);
}`

export const fragment = `
precision highp float;
varying vec3 world;
varying vec3 surfaceNormal;
varying vec4 shadowPosition;
uniform sampler2D shadowMap;
uniform vec3 eye;
uniform vec3 keyLight;
uniform vec3 baseLow;
uniform vec3 baseHigh;
uniform vec3 rimColour;
uniform vec3 fillColour;
uniform float exposure;

float visibility(vec3 n) {
  vec3 p = shadowPosition.xyz / shadowPosition.w * .5 + .5;
  float bias = max(.00045,.0012 * (1. - dot(n,keyLight)));
  float sum = 0.;
  for (int x = -2; x <= 2; x++) {
    for (int y = -2; y <= 2; y++) {
      vec2 uv = p.xy + vec2(float(x),float(y)) * 1.5 / 1024.;
      vec2 encodedDepth = texture2D(shadowMap,uv).rg;
      float depth = encodedDepth.x + encodedDepth.y / 255.;
      sum += p.z - bias <= depth ? 1. : 0.;
    }
  }
  return sum / 25.;
}

vec3 brdf(vec3 n, vec3 v, vec3 l, vec3 base, float shadow) {
  vec3 h = normalize(v+l);
  float nv = max(dot(n,v),.001), nl = max(dot(n,l),0.);
  float nh = max(dot(n,h),0.), vh = max(dot(v,h),0.);
  float rough = .26, a = rough*rough, a2 = a*a, d = nh*nh*(a2-1.)+1.;
  float distribution = a2 / (3.14159*d*d+.0001);
  float k = (rough+1.)*(rough+1.)/8.;
  float geometry = nv/(nv*(1.-k)+k)*nl/(nl*(1.-k)+k);
  vec3 f0 = mix(vec3(.045),base,.42);
  vec3 fresnel = f0 + (1.-f0)*pow(1.-vh,5.);
  vec3 spec = distribution*geometry*fresnel/(4.*nv*max(nl,.001)+.001);
  return ((1.-fresnel)*base*.66/3.14159+spec)*nl*shadow*5.2;
}

void main() {
  vec3 n = normalize(surfaceNormal), v = normalize(eye-world);
  float vis = visibility(n);
  vec3 base = mix(baseLow,baseHigh,smoothstep(-1.73,1.73,world.y));
  float ambient = .16 + .12*max(n.y,0.);
  vec3 color = base*ambient + brdf(n,v,keyLight,base,vis);
  vec3 fill = normalize(vec3(-4.,2.,-5.));
  color += fillColour*max(dot(n,fill),0.);
  float rim = pow(1.-max(dot(n,v),0.),3.);
  color += rimColour*rim*.6;
  float reflected = pow(max(dot(reflect(-v,n),normalize(vec3(5.,5.,1.))),0.),16.);
  color += vec3(.18,.22,.24)*reflected;
  color *= exposure;
  color = color/(color+vec3(.95));
  color = pow(color,vec3(1./2.2));
  gl_FragColor = vec4(color,1.);
}`

export const receiverFragment = `
precision highp float;
varying vec3 world;
varying vec4 shadowPosition;
uniform sampler2D shadowMap;
uniform float shadowOpacity;
uniform float outputSize;
void main() {
  vec3 p = shadowPosition.xyz / shadowPosition.w * .5 + .5;
  if (p.x < .01 || p.x > .99 || p.y < .01 || p.y > .99) {
    gl_FragColor = vec4(0.);
    return;
  }
  float occlusion = 0.;
  for (int x = -2; x <= 2; x++) {
    for (int y = -2; y <= 2; y++) {
      vec2 value = texture2D(shadowMap,p.xy+vec2(float(x),float(y))*6./1024.).rg;
      float depth = value.x + value.y/255.;
      occlusion += p.z-.001 > depth ? 1. : 0.;
    }
  }
  float fade = 1. - smoothstep(1.5,3.2,length(world.xz));
  // The shadow fades before the canvas boundary instead of ending in a visible square.
  vec2 uv = gl_FragCoord.xy / outputSize;
  float edge = min(min(uv.x,1.-uv.x),min(uv.y,1.-uv.y));
  fade *= smoothstep(.01,.18,edge);
  gl_FragColor = vec4(.012,.025,.045,occlusion/25.*shadowOpacity*fade);
}`
