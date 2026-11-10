import type { SendGroupVideoRequest } from "./contracts.js";

export type NormalizedGroupVideoRequest = {
  accountId: string;
  groupId: string;
  idempotencyKey: string | null;
  outbound: SendGroupVideoRequest;
};

export type GroupVideoRequestValidation =
  | { ok: true; value: NormalizedGroupVideoRequest }
  | { ok: false; error: string };

function isAbsoluteHttpUrl(value: unknown): boolean {
  if (typeof value !== "string" || value.trim().length === 0) return false;
  try {
    const url = new URL(value.trim());
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
}

function parsePositiveInteger(value: unknown, field: string): { value: number | null } | { error: string } {
  if (value === undefined || value === null || value === "") return { value: null };
  const number = Number(value);
  if (!Number.isFinite(number) || !Number.isInteger(number) || number <= 0) {
    return { error: `${field} must be a positive integer when provided` };
  }
  return { value: number };
}

export function normalizeGroupVideoRequest(
  body: Partial<SendGroupVideoRequest>,
): GroupVideoRequestValidation {
  const accountId = typeof body.accountId === "string" ? body.accountId.trim() : "";
  const groupId = typeof body.groupId === "string" ? body.groupId.trim() : "";
  if (!accountId || !groupId) return { ok: false, error: "accountId and groupId are required" };

  const inlineVideo = typeof body.videoBase64 === "string" ? body.videoBase64.trim() : "";
  const urlVideo = typeof body.videoUrl === "string" ? body.videoUrl.trim() : "";
  const hasInline = inlineVideo.length > 0;
  const hasUrl = urlVideo.length > 0;
  if (hasInline === hasUrl) return { ok: false, error: "provide exactly one of videoBase64 or videoUrl" };

  const thumbnailUrl = typeof body.thumbnailUrl === "string" ? body.thumbnailUrl.trim() : "";
  const thumbnailBase64 = typeof body.thumbnailBase64 === "string" ? body.thumbnailBase64.trim() : "";
  if (hasUrl && !thumbnailUrl) return { ok: false, error: "thumbnailUrl is required when videoUrl is used" };
  if (hasInline && !thumbnailBase64) return { ok: false, error: "thumbnailBase64 is required when videoBase64 is used" };
  if (hasUrl && (!isAbsoluteHttpUrl(urlVideo) || !isAbsoluteHttpUrl(thumbnailUrl))) {
    return { ok: false, error: "videoUrl and thumbnailUrl must be absolute http(s) URLs" };
  }

  const duration = parsePositiveInteger(body.durationMs, "durationMs");
  if ("error" in duration) return { ok: false, error: duration.error };
  const width = parsePositiveInteger(body.width, "width");
  if ("error" in width) return { ok: false, error: width.error };
  const height = parsePositiveInteger(body.height, "height");
  if ("error" in height) return { ok: false, error: height.error };

  const idempotencyKey = typeof body.idempotencyKey === "string" && body.idempotencyKey.trim()
    ? body.idempotencyKey.trim()
    : null;
  return {
    ok: true,
    value: {
      accountId,
      groupId,
      idempotencyKey,
      outbound: {
        accountId,
        groupId,
        message: body.message ? String(body.message) : null,
        videoUrl: hasUrl ? urlVideo : null,
        thumbnailUrl: thumbnailUrl || null,
        thumbnailBase64: thumbnailBase64 || null,
        thumbnailContentType: body.thumbnailContentType ? String(body.thumbnailContentType) : null,
        thumbnailFileName: body.thumbnailFileName ? String(body.thumbnailFileName) : null,
        videoBase64: hasInline ? inlineVideo : null,
        videoContentType: body.videoContentType ? String(body.videoContentType) : null,
        videoFileName: body.videoFileName ? String(body.videoFileName) : null,
        durationMs: duration.value,
        width: width.value,
        height: height.value,
        idempotencyKey: null,
      },
    },
  };
}
