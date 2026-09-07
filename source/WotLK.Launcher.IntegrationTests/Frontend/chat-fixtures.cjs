const capabilities = ['groups','attachments','markdown','replies','reactions','edit-delete','pins','cards','typing','read-receipts','preferences','link-previews','archive'];
const mediaOrigin = 'https://atlas-chat-media.invalid/';
const self = { accountId: 42, username: 'Aster', presence: 'online', avatarUrl: mediaOrigin + 'avatars/42', characterName: 'Asterion', characterGuid: 942 };
const lyra = { accountId: 91, username: 'Lyra', presence: 'away', avatarUrl: mediaOrigin + 'avatars/91', characterName: 'Lyralune', characterClass: 'Druide', zoneName: 'Dalaran', characterGuid: 991 };
const kael = { accountId: 92, username: 'Kael', presence: 'dnd', avatarUrl: mediaOrigin + 'avatars/92' };
const mira = { accountId: 93, username: 'Mira', presence: 'offline', avatarUrl: mediaOrigin + 'avatars/93' };
const noran = { accountId: 94, username: 'Noran', presence: 'offline' };
const day = new Date(); day.setHours(20, 5, 0, 0);
const at = minutes => new Date(day.getTime() + minutes * 60000).toISOString();
const member = profile => ({ profile, role: profile.accountId === 42 ? 'owner' : 'member', status: 'active', joinedAt: at(-1440), lastReadMessageId: '0' });
const message = (id, sender, body, minutes, extra={}) => ({ id: String(id), threadId: 'd:42:91', clientMessageId: '10000000-0000-4000-8000-' + String(id).slice(-12).padStart(12,'0'), sender, body, origin: 'launcher', createdAt: at(minutes), version:'1', attachments:[],linkPreviews:[],reactions:[],readByAccountIds:[],...extra });
const messages = [
  message('9007199254740993', lyra, 'Salut ! Tu es disponible pour une sortie ce soir ?', 0),
  message('9007199254740994', lyra, 'On pensait partir au **Nexus** vers 21 h, il nous manque juste un soigneur.', 1),
  message('9007199254740995', self, 'Avec plaisir. Je peux venir avec mon prêtre 🙂', 3, {readByAccountIds:[91]}),
  message('9007199254740996', lyra, 'Parfait ! Je vous attends à Dalaran, près de la fontaine.', 4, {origin:'game',senderCharacterName:'Lyralune'}),
  message('9007199254740997', self, 'Je termine ma quête et je vous rejoins.\nLe panorama ici est plutôt sympa !', 8, { attachments:[{id:'fixture-landscape',fileName:'Les montagnes du Norfendre.png',contentType:'image/png',kind:'image',size:'2486000',url:mediaOrigin+'attachments/fixture-landscape'}], reactions:[{emoji:'❤️',accountIds:[91],count:1},{emoji:'🔥',accountIds:[42,91],count:2}]}),
  message('9007199254740998', lyra, 'On a le temps ! Et voilà notre petit rappel pour le donjon :', 10, {linkPreviews:[{id:'guide',url:'https://example.test/nexus',kind:'link',title:'Le Nexus · itinéraire et conseils',description:'Les quatre rencontres, les quêtes à prendre et quelques conseils pour votre première visite.',provider:'Atlas · Guide',canRemove:true,isRemoved:false}]}),
  message('9007199254740999', self, 'Noté. À tout de suite !', 11, {replyTo:{messageId:'9007199254740996',senderUsername:'Lyra',body:'Je vous attends à Dalaran, près de la fontaine.',isDeleted:false}})
];
const threads = [
  { id:'d:42:91',kind:'direct',title:'Lyra',members:[member(self),member(lyra)],lastMessage:messages.at(-1),unreadCount:2,lastReadMessageId:'9007199254740997',isPinned:true,isArchived:false,canSend:true,canManage:false,isInvited:false,version:'1',pinnedMessages:[messages[3]] },
  { id:'group:raid',kind:'group',title:'Les aventuriers du soir',members:[member(self),member(lyra),member(kael),member(mira)],lastMessage:message('9007199254741004',kael,'Tout le monde est prêt pour demain ?',9,{threadId:'group:raid'}),unreadCount:5,lastReadMessageId:'0',isPinned:true,isArchived:false,canSend:true,canManage:true,isInvited:false,version:'3',pinnedMessages:[] },
  { id:'d:42:92',kind:'direct',title:'Kael',members:[member(self),member(kael)],lastMessage:message('9007199254740989',kael,'Merci pour les enchantements !',-18,{threadId:'d:42:92'}),unreadCount:0,lastReadMessageId:'9007199254740989',isPinned:false,isArchived:false,canSend:true,canManage:false,version:'1',pinnedMessages:[] },
  { id:'d:42:93',kind:'direct',title:'Mira',members:[member(self),member(mira)],lastMessage:message('9007199254740960',mira,'À demain 👋',-1440,{threadId:'d:42:93'}),unreadCount:0,lastReadMessageId:'9007199254740960',isPinned:false,isArchived:false,canSend:true,canManage:false,version:'1',pinnedMessages:[] },
  { id:'d:42:94',kind:'direct',title:'Noran',members:[member(self),member(noran)],lastMessage:message('9007199254740800',noran,'On se tient au courant.',-2880,{threadId:'d:42:94'}),unreadCount:0,lastReadMessageId:'9007199254740800',isPinned:false,isArchived:true,canSend:true,canManage:false,version:'1',pinnedMessages:[] }
];
const snapshot = {type:'snapshot',sessionId:'43e20968-4137-4f23-9174-4a097cc6a873',ownerAccountId:42,sequence:'1',locale:'fr',isActive:true,isAvailable:true,isLoading:false,state:{self,contacts:[lyra,kael,mira,noran],threads,preferences:{doNotDisturb:false,shareReadReceipts:true,shareTyping:true,messageSoundEnabled:false},eventCursor:'9007199254740999',capabilities},selectedThreadId:'d:42:91',messages,hasEarlier:true,isLoadingEarlier:false,draft:{body:'',replyToMessageId:null,attachments:[],card:null},pending:[],typing:[],mediaOrigin};
// A 160 x 90 synthetic landscape recorded locally with Chromium VP8 MediaRecorder.
const videoBytes = Buffer.from('GkXfo59ChoEBQveBAULygQRC84EIQoKEd2VibUKHgQRChYECGFOAZwEAAAAAAAHzEU2bdLlNu4tTq4QVSalmU6yBbk27i1OrhBZUrmtTrIGTTbuLU6uEH0O2dVOsgcFNu4xTq4QcU7trU6yCAeHsrgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAVSalmoCrXsYMPQkBEiYQ/gAAATYCGQ2hyb21lV0GGQ2hyb21lFlSua6mup9eBAXPFh0peW3PKd6aDgQFV7oEBhoVWX1ZQOOCKsIGguoFaU8CBAR9DtnUBAAAAAAABFOeBAKBBDqFAyYEAAADwCgCdASqgAFoACocIhYWImYSIOAIZwzoL4BjwMlAfz9+FKmAgA7X+vGcGPvgZ6X+v9ecY8yssuAzBFsBYbgHUp8eIu8bgRBb2KnEwFnh2aaiSQdcqQkSiGUz3A5BogccA/t/QE3PS9aWhck5pXk0S/iB3pmAsEgGC+9vdCyTgjlOrXinMPEHgHjsJv49flaCcHnkKB3rqcuuv1aR10QFY3z7Rj1kSWY1cvwyneq3AjmYgBJo5F7MwkntpBLZbZjD9KLcFAHWhv6a97oEBpbhQBQCdASqgAFoACocIhYWImYSIOAIABigPCHVUmu4h1VJruIdVSa7iHVUmu4h1VJruIbwA/uuuABxTu2uNu4uzgQC3hveBAfGBwQ==','base64');
const audioBytes = Buffer.alloc(16044);
audioBytes.write('RIFF',0);audioBytes.writeUInt32LE(audioBytes.length-8,4);audioBytes.write('WAVEfmt ',8);audioBytes.writeUInt32LE(16,16);audioBytes.writeUInt16LE(1,20);audioBytes.writeUInt16LE(1,22);audioBytes.writeUInt32LE(8000,24);audioBytes.writeUInt32LE(16000,28);audioBytes.writeUInt16LE(2,32);audioBytes.writeUInt16LE(16,34);audioBytes.write('data',36);audioBytes.writeUInt32LE(audioBytes.length-44,40);
const mediaDraft = [
  {id:'preview-image',fileName:'Capture privée.png',contentType:'image/png',size:'500',offset:'500',status:'ready',isComplete:true,previewUrl:mediaOrigin+'attachments/tiny'},
  {id:'preview-video',fileName:'Souvenir privé.webm',contentType:'video/webm',size:String(videoBytes.length),offset:String(videoBytes.length),status:'ready',isComplete:true,previewUrl:mediaOrigin+'attachments/fixture-video.webm'},
  {id:'preview-audio',fileName:'Note privée.wav',contentType:'audio/wav',size:String(audioBytes.length),offset:String(audioBytes.length),status:'ready',isComplete:true,previewUrl:mediaOrigin+'attachments/fixture-audio.wav'}
];
const characterCard = {kind:'character',title:'Asterion',referenceId:'4294967295',fields:{ownerAccountId:'42',characterGuid:'4294967295',level:'80',classId:'6',raceId:'1',realmName:'Arthas'}};
const pending = (suffix, extra={}) => ({clientMessageId:'20000000-0000-4000-8000-'+String(suffix).padStart(12,'0'),threadId:'d:42:91',body:'Envoi local '+suffix,status:'queued',canCancel:true,createdAt:at(40+Number(suffix)),attachments:[],...extra});
module.exports = {snapshot, self, lyra, kael, mira, noran, message, member, at, mediaOrigin, videoBytes, audioBytes, mediaDraft, characterCard, pending};
