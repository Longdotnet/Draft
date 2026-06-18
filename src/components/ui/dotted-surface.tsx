import { useEffect, useRef } from "react";
import * as THREE from "three";
import { cn } from "../../lib/utils";

type DottedSurfaceProps = {
  className?: string;
};

export function DottedSurface({ className }: DottedSurfaceProps) {
  const hostRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) {
      return;
    }

    const element = host;
    const prefersReducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(44, 1, 0.1, 1000);
    const renderer = new THREE.WebGLRenderer({
      alpha: true,
      antialias: true,
      powerPreference: "high-performance",
    });
    const geometry = new THREE.BufferGeometry();
    const material = new THREE.PointsMaterial({
      color: 0x7dd3fc,
      opacity: 0.82,
      size: 0.13,
      sizeAttenuation: true,
      transparent: true,
    });
    const points = new THREE.Points(geometry, material);
    const positionsRef = { current: new Float32Array() };
    const gridRef = { columns: 0, rows: 0, spacing: 0.92 };
    let animationFrame = 0;
    let start = performance.now();

    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.domElement.style.display = "block";
    renderer.domElement.style.width = "100%";
    renderer.domElement.style.height = "100%";
    element.appendChild(renderer.domElement);

    points.rotation.x = -0.74;
    points.rotation.z = -0.08;
    scene.add(points);

    const accentMaterial = new THREE.PointsMaterial({
      color: 0xfb923c,
      opacity: 0.42,
      size: 0.08,
      sizeAttenuation: true,
      transparent: true,
    });
    const accentGeometry = new THREE.BufferGeometry();
    const accentPoints = new THREE.Points(accentGeometry, accentMaterial);
    accentPoints.rotation.copy(points.rotation);
    accentPoints.position.z = 0.15;
    scene.add(accentPoints);

    function rebuildGrid() {
      const width = Math.max(element.clientWidth, 320);
      const height = Math.max(element.clientHeight, 320);
      const columns = Math.min(120, Math.max(52, Math.floor(width / 13)));
      const rows = Math.min(86, Math.max(36, Math.floor(height / 13)));
      const spacing = Math.max(0.7, Math.min(1.05, width / columns / 13));
      const positions = new Float32Array(columns * rows * 3);
      const accentPositions = new Float32Array(Math.ceil(columns * rows * 0.18) * 3);
      let accentIndex = 0;

      gridRef.columns = columns;
      gridRef.rows = rows;
      gridRef.spacing = spacing;

      for (let row = 0; row < rows; row += 1) {
        for (let column = 0; column < columns; column += 1) {
          const index = (row * columns + column) * 3;
          positions[index] = (column - columns / 2) * spacing;
          positions[index + 1] = (row - rows / 2) * spacing;
          positions[index + 2] = 0;

          if ((row + column * 2) % 11 === 0 && accentIndex < accentPositions.length) {
            accentPositions[accentIndex] = positions[index];
            accentPositions[accentIndex + 1] = positions[index + 1];
            accentPositions[accentIndex + 2] = 0.2;
            accentIndex += 3;
          }
        }
      }

      positionsRef.current = positions;
      geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
      accentGeometry.setAttribute(
        "position",
        new THREE.BufferAttribute(accentPositions.slice(0, accentIndex), 3),
      );
      camera.aspect = width / height;
      camera.position.set(0, -2.5, 46);
      camera.updateProjectionMatrix();
      renderer.setSize(width, height, false);
    }

    function updateSurface(time: number) {
      const positions = positionsRef.current;
      const { columns, rows } = gridRef;
      const elapsed = (time - start) * 0.001;

      for (let row = 0; row < rows; row += 1) {
        for (let column = 0; column < columns; column += 1) {
          const index = (row * columns + column) * 3;
          const x = column - columns / 2;
          const y = row - rows / 2;
          positions[index + 2] =
            Math.sin(x * 0.22 + elapsed * 1.15) * 0.72 +
            Math.cos(y * 0.2 + elapsed * 0.9) * 0.5;
        }
      }

      const positionAttribute = geometry.getAttribute("position");
      if (positionAttribute) {
        positionAttribute.needsUpdate = true;
      }
      points.rotation.z = -0.08 + Math.sin(elapsed * 0.16) * 0.025;
      accentPoints.rotation.z = points.rotation.z;
    }

    function render(time: number) {
      if (!prefersReducedMotion) {
        updateSurface(time);
      }
      renderer.render(scene, camera);
      animationFrame = window.requestAnimationFrame(render);
    }

    const resizeObserver = new ResizeObserver(() => {
      rebuildGrid();
      renderer.render(scene, camera);
    });

    rebuildGrid();
    resizeObserver.observe(element);
    animationFrame = window.requestAnimationFrame(render);

    return () => {
      window.cancelAnimationFrame(animationFrame);
      resizeObserver.disconnect();
      geometry.dispose();
      material.dispose();
      accentGeometry.dispose();
      accentMaterial.dispose();
      renderer.dispose();
      renderer.domElement.remove();
      start = 0;
    };
  }, []);

  return <div ref={hostRef} className={cn("pointer-events-none absolute inset-0", className)} />;
}
