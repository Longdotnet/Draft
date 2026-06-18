import { useEffect, useRef } from "react";
import * as THREE from "three";
import { cn } from "../../lib/utils";

type AnimatedShaderBackgroundProps = {
  className?: string;
};

const vertexShader = `
  varying vec2 vUv;

  void main() {
    vUv = uv;
    gl_Position = vec4(position.xy, 0.0, 1.0);
  }
`;

const fragmentShader = `
  precision highp float;

  uniform float u_time;
  uniform vec2 u_resolution;
  varying vec2 vUv;

  float orb(vec2 uv, vec2 center, float radius, float softness) {
    float dist = length(uv - center);
    return smoothstep(radius, radius - softness, dist);
  }

  void main() {
    vec2 uv = vUv;
    vec2 p = uv * 2.0 - 1.0;
    p.x *= u_resolution.x / max(u_resolution.y, 1.0);

    float waveA = sin((p.x * 2.4 + u_time * 0.26) + sin(p.y * 3.0)) * 0.5 + 0.5;
    float waveB = cos((p.y * 2.8 - u_time * 0.22) + cos(p.x * 2.2)) * 0.5 + 0.5;
    float ribbon = smoothstep(0.22, 0.95, waveA * waveB);

    vec2 centerA = vec2(0.42 + sin(u_time * 0.16) * 0.18, 0.34 + cos(u_time * 0.13) * 0.12);
    vec2 centerB = vec2(0.70 + cos(u_time * 0.11) * 0.12, 0.72 + sin(u_time * 0.18) * 0.10);
    float glowA = orb(uv, centerA, 0.62, 0.46);
    float glowB = orb(uv, centerB, 0.48, 0.36);

    vec3 deep = vec3(0.015, 0.025, 0.075);
    vec3 teal = vec3(0.02, 0.42, 0.62);
    vec3 violet = vec3(0.35, 0.09, 0.54);
    vec3 gold = vec3(0.95, 0.58, 0.12);

    vec3 color = deep;
    color = mix(color, teal, glowA * 0.55);
    color = mix(color, violet, glowB * 0.58);
    color = mix(color, gold, ribbon * 0.10);

    float vignette = smoothstep(1.45, 0.24, length(p));
    vec2 starGrid = uv * 120.0;
    vec2 starCell = floor(starGrid);
    vec2 starLocal = fract(starGrid) - 0.5;
    float starSeed = fract(sin(dot(starCell, vec2(12.9898, 78.233))) * 43758.5453);
    float starDot = smoothstep(0.18, 0.0, length(starLocal));
    float stars = step(0.992, starSeed) * starDot;
    stars *= 0.18 + 0.82 * (sin(u_time * 1.7 + starSeed * 12.0) * 0.5 + 0.5);

    color *= 0.52 + vignette * 0.78;
    color += vec3(stars) * vec3(0.62, 0.85, 1.0);

    gl_FragColor = vec4(color, 1.0);
  }
`;

function AnimatedShaderBackground({ className }: AnimatedShaderBackgroundProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    let frameId = 0;
    let renderer: THREE.WebGLRenderer;

    try {
      renderer = new THREE.WebGLRenderer({
        antialias: true,
        alpha: true,
        powerPreference: "high-performance",
      });
    } catch {
      return;
    }

    const scene = new THREE.Scene();
    const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
    const geometry = new THREE.PlaneGeometry(2, 2);
    const material = new THREE.ShaderMaterial({
      vertexShader,
      fragmentShader,
      uniforms: {
        u_time: { value: 0 },
        u_resolution: { value: new THREE.Vector2(1, 1) },
      },
      depthWrite: false,
      depthTest: false,
    });
    const plane = new THREE.Mesh(geometry, material);

    scene.add(plane);
    renderer.setClearColor(0x000000, 0);
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    container.appendChild(renderer.domElement);

    const resize = () => {
      const width = Math.max(container.clientWidth, 1);
      const height = Math.max(container.clientHeight, 1);
      renderer.setSize(width, height, false);
      material.uniforms.u_resolution.value.set(width, height);
    };

    const animate = (time: number) => {
      material.uniforms.u_time.value = time * 0.001;
      renderer.render(scene, camera);
      frameId = window.requestAnimationFrame(animate);
    };

    resize();
    window.addEventListener("resize", resize);
    frameId = window.requestAnimationFrame(animate);

    return () => {
      window.cancelAnimationFrame(frameId);
      window.removeEventListener("resize", resize);
      scene.remove(plane);
      geometry.dispose();
      material.dispose();
      renderer.dispose();

      if (renderer.domElement.parentNode === container) {
        container.removeChild(renderer.domElement);
      }
    };
  }, []);

  return (
    <div
      ref={containerRef}
      className={cn(
        "absolute inset-0 h-full w-full overflow-hidden bg-[radial-gradient(circle_at_50%_35%,rgba(14,165,233,0.24),transparent_35%),linear-gradient(135deg,#020617,#111827_46%,#2e1065)]",
        className,
      )}
    />
  );
}

export default AnimatedShaderBackground;
