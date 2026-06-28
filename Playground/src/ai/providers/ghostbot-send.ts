/** Trystero broadcast sentinel — send to all peers in the room (see Trystero README). */
export const GHOST_BROADCAST_TARGET = null;

export function broadcastGhostSend(
    send: (data: Uint8Array, target: string | null | undefined) => void | Promise<unknown>,
    bytes: Uint8Array,
): void {
    void send(bytes, GHOST_BROADCAST_TARGET);
}
