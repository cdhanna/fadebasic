#version 300 es
precision highp float;

in lowp vec2 vUV;
uniform float uTime;
uniform float uFrame64;
uniform sampler2D uSampler;
uniform vec2 uResolution;

out vec4 outColor;

vec4 uvMask(vec2 uv, vec4 data) {
    if (data.a> .1){
        return vec4(uv.x, uv.y, 0., 1.);
    } 
    return vec4(0.);
}

vec4 read(vec2 uv, sampler2D tex){
    return texture(tex, uv);
}

vec4 jfa(vec2 uv, float stepSize, sampler2D tex){
    float bestDistance = 99999.;
    vec2 bestUv = vec2(0.);
    // uv = (floor((uv * uResolution)) + .5) / uResolution;

    // https://www.shadertoy.com/view/4syGWK 
    for (int x = -1; x <= 1; x++) {
        for (int y = -1; y <= 1; y++) {
            vec2 dir = vec2(x, y);
            vec2 offset = uv + dir * stepSize;

            // offset -= .5 * 1./uResolution;
            vec2 temp = read(offset, tex).xy;
            // vec2 temp = uvMask(offset, read(offset, tex)).xy;
            // vec2 temp = uvMask(uv, read(offset, tex)).xy;
        
            float dist = length(temp - uv);

            if (temp.x != 0. && temp.y != 0. && dist < bestDistance){
                bestDistance = dist;
                bestUv = temp;
            }
        }
    }

    return vec4(bestUv.x, bestUv.y, 0., 1.);
}




float rand(vec2 n) {
    return fract(sin(cos(dot(n, vec2(12.9898,12.1414)))) * 83758.5453);
}

float noise(vec2 n) {
    const vec2 d = vec2(0.0, 1.0);
    vec2 b = floor(n), f = smoothstep(vec2(0.0), vec2(1.0), fract(n));
    return mix(mix(rand(b), rand(b + d.yx), f.x), mix(rand(b + d.xy), rand(b + d.yy), f.x), f.y);
}

float fbm(vec2 n) {
    float total = 0.0, amplitude = 1.0;
    for (int i = 0; i <5; i++) {
        total += noise(n) * amplitude;
        n += n*1.7;
        amplitude *= 0.47;
    }
    return total;
}

void fire( out vec4 fragColor, in vec2 fragCoord ) {

    const vec3 c1 = vec3(0.5, 0.0, 0.1);
    const vec3 c2 = vec3(0.9, 0.1, 0.0);
    const vec3 c3 = vec3(0.2, 0.1, 0.7);
    const vec3 c4 = vec3(1.0, 0.9, 0.1);
    const vec3 c5 = vec3(0.1);
    const vec3 c6 = vec3(0.9);

    vec2 speed = vec2(0.1, 0.9);
    float shift = 1.327+sin(uTime*2.0)/2.4;
    float alpha = 1.0;
    
	float dist = 3.5-sin(uTime*0.4)/1.89;
    
    vec2 uv = fragCoord.xy / uResolution.xy;
    vec2 p = fragCoord.xy * dist / uResolution.xx;
    p += sin(p.yx*4.0+vec2(.2,-.3)*uTime)*0.04;
    p += sin(p.yx*8.0+vec2(.6,+.1)*uTime)*0.01;
    
    p.x -= uTime/1.1;
    float q = fbm(p - uTime * 0.3+1.0*sin(uTime+0.5)/2.0);
    float qb = fbm(p - uTime * 0.4+0.1*cos(uTime)/2.0);
    float q2 = fbm(p - uTime * 0.44 - 5.0*cos(uTime)/2.0) - 6.0;
    float q3 = fbm(p - uTime * 0.9 - 10.0*cos(uTime)/15.0)-4.0;
    float q4 = fbm(p - uTime * 1.4 - 20.0*sin(uTime)/14.0)+2.0;
    q = (q + qb - .4 * q2 -2.0*q3  + .6*q4)/3.8;
    vec2 r = vec2(fbm(p + q /2.0 + uTime * speed.x - p.x - p.y), fbm(p + q - uTime * speed.y));
    vec3 c = mix(c1, c2, fbm(p + r)) + mix(c3, c4, r.x) - mix(c5, c6, r.y);
    vec3 color = vec3(1.0/(pow(c+1.61,vec3(4.0))) * cos(shift * fragCoord.y / uResolution.y));
    
    color=vec3(1.0,.2,.05)/(pow((r.y+r.y)* max(.0,p.y)+0.1, 4.0));;
    color += (texture(uSampler,uv*0.6+vec2(.5,.1)).xyz*0.01*pow((r.y+r.y)*.65,5.0)+0.055)*mix( vec3(.9,.4,.3),vec3(.7,.5,.2), uv.y);
    color = color/(1.0+max(vec3(0),color));
    fragColor = vec4(color.x, color.y, color.z, alpha);
}



void main() {
    float uTime = uTime * 12.0;
    vec2 uv = vUV;

    vec4 fragColor = vec4(0.);

// TODO: ping-pong textures are almost working; the seed texture inits every 16th frame.
//       

    // fragColor = uvMask(uv, fragColor);


    if (uFrame64 < 1.){

        fragColor = uvMask(uv, read(uv, uSampler));
        // fragColor = read(uv, uSampler);

    }
    else if (uFrame64 < 99.) {
        // uv.y = 1. - uv.y;

        float stepSize = 1./exp2(uFrame64);
        // fragColor.r = stepSize;
        // fragColor.a = 1.;

        fragColor = jfa(uv, stepSize, uSampler);
        // fragColor.rg -= .
        // fragColor.a = 1.;
        // fragColor.rgb = vec3(stepSize);
        

    } else {
        
        uv.y = 1. - uv.y;
        vec4 data = read(uv, uSampler);

        // fragColor = data;
        // fragColor.b = 1.;
        // fragColor.a = 1.;
        float f = length(data.xy-uv);

        fragColor.a = 1.;
        fragColor.rgb = vec3(fract(f * 15.));
        // float c =  fract(f * 5. + uTime*2.);

        // vec2 fireUv = uv;
        // fireUv.y = 1. - fireUv.y;
        // fireUv /= f;
        // vec4 fireColor = vec4(0.);
        // fire(fireColor, fireUv*uResolution + uTime*2000.);
        // fragColor.rgb = fireColor.rgb;
    }

    outColor = fragColor;
}