using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using SQLite;
using MonoTouch.UIKit;
using MonoTouch.Foundation;
using System.Text;

namespace TweetStation
{
	public enum TweetKind {
		Home,
		Replies,
		Direct,
		Transient,
	}
	
	public class TwitterAccount
	{
		const string timelineUri = "http://api.twitter.com/1/statuses/home_timeline.json";
		const string mentionsUri = "http://api.twitter.com/1/statuses/mentions.json";
		const string directUri = "http://api.twitter.com/1/direct_messages.json";
			
		const string DEFAULT_ACCOUNT = "defaultAccount";
		
		[PrimaryKey, AutoIncrement]
		public int LocalAccountId { get; set; }
		
		public long AccountId { get; set; }
		public long LastLoaded { get; set; }
		public string Username { get; set; }
		public string Password { get; set; }
		
		static NSString invoker = new NSString ("");
		
		static Dictionary<long, TwitterAccount> accounts = new Dictionary<long, TwitterAccount> ();
		
		public static TwitterAccount FromId (int id)
		{
			if (accounts.ContainsKey (id)){
				return accounts [id];
			}
			
			var account = Database.Main.Query<TwitterAccount> ("select * from TwitterAccount where LocalAccountId = ?", id).FirstOrDefault ();
			if (account != null)
				accounts [account.LocalAccountId] = account;
			
			return account;
		}
		
		public static TwitterAccount CurrentAccount { get; set; }
		
		public static TwitterAccount GetDefaultAccount ()
		{			
			if (File.Exists ("/Users/miguel/tpass")){
				using (var f = System.IO.File.OpenText ("/Users/miguel/tpass")){
					var ta = new TwitterAccount () { 
						Username = f.ReadLine (),
						Password = f.ReadLine ()
					};
					Database.Main.Insert (ta, "OR IGNORE");
					using (var f2 = File.OpenRead ("home_timeline.json")){
						Tweet.LoadJson (f2, ta.LocalAccountId, TweetKind.Home);
					}
					accounts [ta.LocalAccountId] = ta;
					CurrentAccount = ta;
					return ta;
				}
			}
			
			var account = FromId (Util.Defaults.IntForKey (DEFAULT_ACCOUNT));
			CurrentAccount = account;
			return account;
		}

		public void ReloadTimeline (TweetKind kind, long? since, long? max_id, Action<int> done)
		{
			string uri = null;
			switch (kind){
			case TweetKind.Home:
				uri = timelineUri; break;
			case TweetKind.Replies:
				uri = mentionsUri; break;
			case TweetKind.Direct:
				uri = directUri; break;
			}
			var req = new Uri (uri + "?count=200" + 
			                   (since.HasValue ? "&since_id=" + since.Value : "") +
			                   (max_id.HasValue ? "&max_id=" + max_id.Value : ""));
			
			Download (req, result => {
				if (result == null)
					done (-1);
				else {
					int count = -1;
					try {
						count = Tweet.LoadJson (new MemoryStream (result), LocalAccountId, kind);
					} catch (Exception e) { 
						Console.WriteLine (e);
					}
					done (count);
				}
			});
		}
		
		internal struct Request {
			public Uri Url;
			public Action<byte []> Callback;
			
			public Request (Uri url, Action<byte []> callback)
			{
				Url = url;
				Callback = callback;
			}
		}
		
		const int MaxPending = 200;
		static Queue<Request> queue = new Queue<Request> ();
		static int pending;
		
		/// <summary>
		///   Throttled data download from the specified url and invokes the callback with
		///   the resulting data on the main UIKit thread.
		/// </summary>
		public void Download (Uri url, Action<byte []> callback)
		{
			lock (queue){				
				pending++;
				if (pending++ < MaxPending)
					Launch (url, callback);
				else {
					queue.Enqueue (new Request (url, callback));
					//Console.WriteLine ("Queued: {0}", url);
				}
			}
		}

		// This is required because by default WebClient wont authenticate
		// until challenged to.   Twitter does not do that, so we need to force
		// the pre-authentication
		class AuthenticatedWebClient : WebClient {
			protected override WebRequest GetWebRequest (Uri address)
			{
				var req = (HttpWebRequest) WebRequest.Create (address);
				req.PreAuthenticate = true;
				
				return req;
			}
		}
		
		WebClient GetClient ()
		{
			return new AuthenticatedWebClient (){
				Credentials = new NetworkCredential (Username, Password),
			};
		}
		
		void Launch (Uri url, Action<byte []> callback)
		{
			var client = GetClient ();
	
			client.DownloadDataCompleted += delegate(object sender, DownloadDataCompletedEventArgs e) {
				lock (queue)
					pending--;
				
				Util.PopNetworkActive ();
				
				invoker.BeginInvokeOnMainThread (delegate {
					try {
						if (e == null)
							callback (null);
						callback (e.Result);
					} catch  (Exception ex){
						Console.WriteLine (ex);
					}
				});
				
				lock (queue){
					if (queue.Count > 0){
						var request = queue.Dequeue ();
						Launch (request.Url, request.Callback);
					}
				}
			};
			Util.PushNetworkActive ();
			Console.WriteLine ("Fetching: {0}", url);
			client.DownloadDataAsync (url);
		}
		
		public void SetDefaultAccount ()
		{
			NSUserDefaults.StandardUserDefaults.SetInt (LocalAccountId, DEFAULT_ACCOUNT); 
		}
		
		// 
		// Posts the @contents to the @url.   The post is done in a queue
		// system that is flushed regularly, so it is safe to call Post to
		// fire and forget
		//
		public void Post (string url, string content)
		{
			var qtask = new QueuedTask () {
				AccountId = LocalAccountId, 
				Url = url, 
				PostData = content,
			};
			Database.Main.Insert (qtask);
			
			FlushTasks ();
		}
		
		void FlushTasks ()
		{
			var tasks = Database.Main.Query<QueuedTask> ("SELECT * FROM QueuedTask ORDER BY TaskId DESC").ToArray ();	
			ThreadPool.QueueUserWorkItem (delegate { PostTask (tasks); });
		}
		
		// 
		// TODO ITEMS:
		//   * Need to change this to use HttpWebRequest, since I need to erad
		//     the result back and create a tweet out of it, and insert in DB.
		//
		//   * Report error to the user?   Perhaps have a priority flag
		//     (posts show dialog, btu starring does not?
		//
		// Runs on a thread from the threadpool.
		void PostTask (QueuedTask [] tasks)
		{
			var client = GetClient ();
			try {
				Util.PushNetworkActive ();
				foreach (var task in tasks){
					client.UploadData (new Uri (task.Url), "POST", Encoding.UTF8.GetBytes (task.PostData));
					invoker.BeginInvokeOnMainThread (delegate {
						try {
							Database.Main.Execute ("DELETE FROM QueuedTask WHERE TaskId = ?", task.TaskId);
						} catch (Exception e){
							Console.WriteLine (e);
						}
					});	
				}
			} catch (Exception e) {
				Console.WriteLine (e);
			} finally {
				Util.PopNetworkActive ();
			}
		}
		
		public class QueuedTask {
			[PrimaryKey, AutoIncrement]
			public int TaskId { get; set; }
			public long AccountId { get; set; }
			public string Url { get; set; }

			public string PostData { get; set; }
		}
	}
	
	public interface IAccountContainer {
		TwitterAccount Account { get; set; }
	}
}
