using MySqlConnector;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    private Task<bool> ChangeV2FriendshipAsync(uint account,uint friend,bool accept,CancellationToken token)=>MutateV2Async(async(c,t)=>
    {
        uint low=Math.Min(account,friend),high=Math.Max(account,friend);
        long changed=await V2ExecuteAsync(c,t,accept?"""
            UPDATE atlas_launcher_friendship SET accepted_at=UTC_TIMESTAMP(),updated_at=UTC_TIMESTAMP()
            WHERE account_low_id=@low AND account_high_id=@high AND requested_by_id=@friend AND accepted_at IS NULL;
            """:"DELETE FROM atlas_launcher_friendship WHERE account_low_id=@low AND account_high_id=@high;",token,("@low",low),("@high",high),("@friend",friend));
        if(changed>0)await NotifyV2FriendshipAsync(c,t,low,high,accept,token);return changed>0;
    },token);

    private static async Task NotifyV2FriendshipAsync(MySqlConnection c,MySqlTransaction t,uint low,uint high,bool accepted,CancellationToken token)
    {
        long thread=await V2ScalarAsync(c,t,"SELECT id FROM atlas_launcher_chat_v2_thread WHERE account_low_id=@low AND account_high_id=@high;",token,("@low",low),("@high",high));
        if(thread>0)await EmitV2Async(c,t,accepted?"thread":"access",thread,null,null,token);
    }
}
