import assert from "node:assert/strict";
import test from "node:test";
import {
  sendGroupVideoWithApi,
  type NativeVideoApi,
} from "../src/zaloGateway.js";

test("inline video uploads MP4 and thumbnail then uses native sendVideo", async () => {
  const uploads: Array<{ filename: string; groupId: string; type: number }> = [];
  const nativeCalls: Array<{
    options: {
      msg?: string;
      videoUrl: string;
      thumbnailUrl: string;
      duration?: number;
      width?: number;
      height?: number;
    };
    groupId: string;
    type: number;
  }> = [];

  const api: NativeVideoApi = {
    async uploadAttachment(source, groupId, type) {
      const value = Array.isArray(source) ? source[0] : source;
      assert.equal(typeof value, "object");
      if (typeof value !== "object" || value === null || !("filename" in value)) {
        throw new Error("expected inline attachment");
      }
      uploads.push({ filename: value.filename, groupId, type });
      return value.filename.endsWith(".mp4")
        ? [{ fileType: "video", fileUrl: "https://zalo.example/uploaded-video.mp4" }]
        : [{ fileType: "image", normalUrl: "https://zalo.example/uploaded-thumb.jpg" }];
    },
    async sendVideo(options, groupId, type) {
      nativeCalls.push({ options, groupId, type });
      return { msgId: 4242 };
    },
  };

  const result = await sendGroupVideoWithApi(api, {
    accountId: "account-1",
    groupId: "group-1",
    message: "NPC PICK",
    videoBase64: Buffer.from("fake-mp4").toString("base64"),
    videoContentType: "video/mp4",
    videoFileName: "story.mp4",
    thumbnailBase64: Buffer.from("fake-jpeg").toString("base64"),
    thumbnailContentType: "image/jpeg",
    thumbnailFileName: "story.jpg",
    durationMs: 20_000,
    width: 720,
    height: 1280,
  }, "group-1");

  assert.deepEqual(uploads.map((item) => item.filename).sort(), ["story.jpg", "story.mp4"]);
  const nativeCall = assertSingle(nativeCalls);
  assert.equal(nativeCall.groupId, "group-1");
  assert.equal(nativeCall.options.videoUrl, "https://zalo.example/uploaded-video.mp4");
  assert.equal(nativeCall.options.thumbnailUrl, "https://zalo.example/uploaded-thumb.jpg");
  assert.equal(nativeCall.options.msg, "NPC PICK");
  assert.equal(nativeCall.options.duration, 20_000);
  assert.equal(nativeCall.options.width, 720);
  assert.equal(nativeCall.options.height, 1280);
  assert.deepEqual(result, { sent: true, mock: false, messageId: "4242" });
});

test("URL video transport goes straight to native sendVideo without upload", async () => {
  let uploadCount = 0;
  let sentVideoUrl: string | null = null;
  const api: NativeVideoApi = {
    async uploadAttachment() {
      uploadCount += 1;
      return [];
    },
    async sendVideo(options) {
      sentVideoUrl = options.videoUrl;
      return { msgId: 7 };
    },
  };

  const result = await sendGroupVideoWithApi(api, {
    accountId: "account-1",
    groupId: "group-1",
    videoUrl: "https://cdn.example/story.mp4",
    thumbnailUrl: "https://cdn.example/story.jpg",
  }, "group-1");

  assert.equal(uploadCount, 0);
  assert.equal(sentVideoUrl, "https://cdn.example/story.mp4");
  assert.equal(result.messageId, "7");
});

test("inline upload fails closed when Zalo does not return playable URLs", async () => {
  const api: NativeVideoApi = {
    async uploadAttachment(source) {
      const value = Array.isArray(source) ? source[0] : source;
      return typeof value === "object" && value !== null && "filename" in value && value.filename.endsWith(".mp4")
        ? [{ fileType: "video" }]
        : [{ fileType: "image" }];
    },
    async sendVideo() {
      throw new Error("sendVideo should not run");
    },
  };

  await assert.rejects(
    sendGroupVideoWithApi(api, {
      accountId: "account-1",
      groupId: "group-1",
      videoBase64: Buffer.from("fake-mp4").toString("base64"),
      thumbnailBase64: Buffer.from("fake-jpeg").toString("base64"),
    }, "group-1"),
    /playable uploaded video\/thumbnail URLs/,
  );
});

function assertSingle<T>(items: T[]): T {
  assert.equal(items.length, 1);
  return items[0]!;
}
