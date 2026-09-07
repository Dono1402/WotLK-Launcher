const REQUEST_PREFIX = 'ATLAS_ARMORY_REQUEST ';
const MAX_LINE_BYTES = 4*1024*1024;
const MAX_REQUEST_BYTES = 4096;

function rpcError(reason,code='ARMORY_RPC_UNAVAILABLE') {
  return Object.assign(new Error(reason),{code});
}

function createLauncherRpc({input=process.stdin,output=process.stdout,onShutdown=() => {},
  timeoutMs=30000,maxConcurrent=4,maxLineBytes=MAX_LINE_BYTES}={}) {
  if (!Number.isInteger(timeoutMs) || timeoutMs<1 || !Number.isInteger(maxConcurrent) || maxConcurrent<1 || maxConcurrent>4
      || !Number.isInteger(maxLineBytes) || maxLineBytes<32 || maxLineBytes>MAX_LINE_BYTES) throw new Error('Invalid armory RPC limits');
  let nextId = 1,closed = false,buffer = Buffer.alloc(0);
  const pending = new Map();

  function finish(id,error,result) {
    const request = pending.get(id);
    if (!request) return;
    pending.delete(id);
    clearTimeout(request.timer);
    request.signal?.removeEventListener('abort',request.cancel);
    if (error) request.reject(error); else request.resolve(result);
  }
  function close(error=rpcError('Armory bridge closed')) {
    if (closed) return;
    closed = true;
    buffer = Buffer.alloc(0);
    input.off('data',receive);
    input.off('end',ended);
    input.off('error',failed);
    output.off('error',failed);
    for (const id of pending.keys()) finish(id,error);
  }
  function shutdown(error) {
    if (closed) return;
    close(error);
    onShutdown();
  }
  function ended() { shutdown(rpcError('Armory bridge input ended')); }
  function failed() { shutdown(rpcError('Armory bridge transport failed')); }
  function lineReceived(bytes) {
    let text;
    try { text = new TextDecoder('utf-8',{fatal:true}).decode(bytes).replace(/\r$/,''); }
    catch { shutdown(rpcError('Invalid armory bridge response encoding')); return; }
    if (text==='shutdown') { shutdown(); return; }
    if (!text.length) return;
    let message;
    try { message = JSON.parse(text); }
    catch { shutdown(rpcError('Invalid armory bridge response')); return; }
    if (!message || typeof message!=='object' || Array.isArray(message) || !Number.isSafeInteger(message.id) || message.id<1) {
      shutdown(rpcError('Invalid armory bridge response')); return;
    }
    if (!pending.has(message.id)) return; // A completed or cancelled response may arrive later.
    const result = Object.hasOwn(message,'result'),error = Object.hasOwn(message,'error');
    if (result===error || (result && (!message.result || typeof message.result!=='object' || Array.isArray(message.result)))
        || (error && !['unavailable','unauthorized'].includes(message.error))) {
      finish(message.id,rpcError('Invalid armory bridge response')); return;
    }
    if (error) {
      const failure = rpcError('Armory data '+message.error,
        message.error==='unauthorized' ? 'ARMORY_RPC_UNAUTHORIZED' : 'ARMORY_RPC_UNAVAILABLE');
      finish(message.id,failure);
      if (message.error==='unauthorized') shutdown(failure);
    }
    else finish(message.id,null,message.result);
  }
  function receive(chunk) {
    if (closed) return;
    const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk,'utf8');
    let start = 0;
    while (start<bytes.length && !closed) {
      const newline = bytes.indexOf(10,start);
      const end = newline<0 ? bytes.length : newline;
      const part = bytes.subarray(start,end);
      if (buffer.length+part.length>maxLineBytes) { shutdown(rpcError('Armory bridge response is too large')); return; }
      buffer = buffer.length ? Buffer.concat([buffer,part]) : Buffer.from(part);
      if (newline<0) return;
      const line = buffer;
      buffer = Buffer.alloc(0);
      lineReceived(line);
      start = newline+1;
    }
  }
  input.on('data',receive);
  input.on('end',ended);
  input.on('error',failed);
  output.on('error',failed);

  function request(operation,characterId,{signal}={}) {
    if (!['roster','catalog'].includes(operation) || (operation==='roster' && characterId!==undefined)
        || (operation==='catalog' && (!Number.isSafeInteger(characterId) || characterId<1 || characterId>0xffffffff)))
      return Promise.reject(rpcError('Invalid armory request','ARMORY_RPC_INVALID_REQUEST'));
    if (closed) return Promise.reject(rpcError('Armory bridge closed'));
    if (signal?.aborted) return Promise.reject(signal.reason || new DOMException('Aborted','AbortError'));
    if (pending.size>=maxConcurrent) return Promise.reject(rpcError('Too many armory requests','ARMORY_RPC_CONCURRENCY_LIMIT'));
    const id = nextId++;
    const line = REQUEST_PREFIX+JSON.stringify({id,operation,...operation==='catalog' ? {characterId} : {}})+'\n';
    if (Buffer.byteLength(line)>MAX_REQUEST_BYTES) return Promise.reject(rpcError('Armory request is too large','ARMORY_RPC_INVALID_REQUEST'));
    return new Promise((resolve,reject) => {
      const cancel = () => finish(id,signal.reason || new DOMException('Aborted','AbortError'));
      const timer = setTimeout(() => finish(id,rpcError('Armory request timed out','ARMORY_RPC_TIMEOUT')),timeoutMs);
      pending.set(id,{resolve,reject,timer,signal,cancel});
      signal?.addEventListener('abort',cancel,{once:true});
      try {
        output.write(line);
      } catch { finish(id,rpcError('Armory bridge transport failed')); }
    });
  }
  return {request,close,get pendingCount() { return pending.size; },
    loadRoster:options => request('roster',undefined,options),
    loadCatalog:(characterId,options) => request('catalog',characterId,options)};
}

module.exports = {createLauncherRpc,REQUEST_PREFIX,MAX_LINE_BYTES,MAX_REQUEST_BYTES};
