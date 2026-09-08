export class KeyedSerialExecutor {
  private readonly tails = new Map<string, Promise<void>>();

  async run<T>(keyValue: string, operation: () => Promise<T>): Promise<T> {
    const key = keyValue.trim();
    if (!key) return operation();

    const previous = this.tails.get(key) ?? Promise.resolve();
    let release!: () => void;
    const turn = new Promise<void>((resolve) => {
      release = resolve;
    });
    const tail = previous.then(() => turn, () => turn);
    this.tails.set(key, tail);

    await previous.catch(() => undefined);
    try {
      return await operation();
    } finally {
      release();
      void tail.finally(() => {
        if (this.tails.get(key) === tail) this.tails.delete(key);
      });
    }
  }
}
