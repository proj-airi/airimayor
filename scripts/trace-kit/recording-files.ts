import type { LocalDirectory } from './viewer-model'

interface DirectoryHandle {
  name: string
  getDirectoryHandle: (name: string) => Promise<DirectoryHandle>
  getFileHandle: (name: string) => Promise<{ getFile: () => Promise<{ size: number; arrayBuffer: () => Promise<ArrayBuffer> }> }>
}

export function recordingDirectory(root: DirectoryHandle): LocalDirectory {
  const directories = new Map<string, Promise<DirectoryHandle>>()
  return { name: root.name, file: async path => {
    try {
      const parts = path.split('/')
      let directory = root
      let prefix = ''
      for (const part of parts.slice(0, -1)) {
        prefix += `${part}/`
        let handle = directories.get(prefix)
        if (!handle) {
          handle = directory.getDirectoryHandle(part)
          directories.set(prefix, handle)
        }
        directory = await handle
      }
      const file = await (await directory.getFileHandle(parts.at(-1)!)).getFile()
      return { path, size: file.size, read: async () => new Uint8Array(await file.arrayBuffer()) }
    }
    catch (error) {
      if (error instanceof DOMException && error.name === 'NotFoundError') return
      throw error
    }
  } }
}
