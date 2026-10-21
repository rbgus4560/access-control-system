// Run after: npm install --prefix <temporary folder> @electric-sql/pglite
// PGLITE_MODULE points at that folder's .../@electric-sql/pglite/dist/index.js.
// Executes the production UPSERT in an isolated PostgreSQL WASM engine, not the user's DB.
import {readFile} from 'node:fs/promises';
const {PGlite}=await import(process.env.PGLITE_MODULE || '@electric-sql/pglite');
const db=new PGlite();
await db.exec(`CREATE SCHEMA access_control;
CREATE TABLE access_control.client_instances(client_id int PRIMARY KEY,client_code text,app_type text);
CREATE TABLE access_control.cameras(camera_id int PRIMARY KEY,client_id int,camera_code text,purpose text);
CREATE TABLE access_control.recording_segments(segment_id uuid PRIMARY KEY,camera_id int,start_time timestamptz,end_time timestamptz,file_path text,status text,file_size_bytes bigint,deleted_at timestamptz,deletion_reason text,client_metadata jsonb);
INSERT INTO access_control.client_instances VALUES(1,'RECOGNITION-01','RECOGNITION');
INSERT INTO access_control.cameras VALUES(1,1,'CAM-01','AUTHENTICATION');`);
const source=await readFile(new URL('../AccessControlServer/RecordingDatabase.cs',import.meta.url),'utf8');
const sql=source.match(/new\s+NpgsqlCommand\s*\(\s*"""([\s\S]*?)"""\s*,\s*connection\s*\)/)[1];
const names=[];const query=sql.replace(/@([a-z_]+)/g,(_,name)=>{if(!names.includes(name))names.push(name);return '$'+(names.indexOf(name)+1)});
let values={id:'00000000-0000-0000-0000-000000000001',start:'2026-10-19T00:00:00Z',end:null,path:'C:\\trusted\\old.mp4',status:'RECORDING',size:null,deleted:null,reason:null,meta:'{}',remote:false,legacy_path:'C:\\trusted\\old.mp4'};
const save=async(v={})=>(await db.query(query,names.map(n=>({...values,...v})[n]))).rows;
const check=(ok,message)=>{if(!ok)throw new Error(message);console.log(message+': PASS')};
check((await save()).length===1,'Legacy RECORDING insert');
values={...values,remote:true,path:'client://CAM-01/00000000000000000000000000000001',status:'READY',end:'2026-10-19T00:01:00Z',size:1234};
check((await save()).length===1,'Canonical legacy path migrates on RECORDING to READY');
check((await db.query('SELECT file_path FROM access_control.recording_segments')).rows[0].file_path===values.path,'Remote locator persisted');
check((await save()).length===1,'Repeated READY is idempotent');
check((await save({start:'2026-10-19T00:00:01Z'})).length===0,'Different start is rejected');
check((await save({status:'RECORDING',end:null,size:null})).length===0,'READY regression rejected');
await db.query('UPDATE access_control.recording_segments SET file_path=$1',['C:\\untrusted\\other.mp4']);
check((await save()).length===0,'Unrelated existing path is preserved');
await db.query('UPDATE access_control.recording_segments SET file_path=$1',[values.legacy_path]);
check((await save({remote:false})).length===0,'Local caller cannot perform remote migration');
check((await save()).length===1,'Completed legacy READY migrates without losing identity');
check((await save({status:'DELETED',deleted:'2026-10-22T00:01:01Z',reason:'RETENTION_72H'})).length===1,'READY deletion transition retained');
check((await save()).length===0,'Deleted recording cannot be resurrected');
await db.close();
