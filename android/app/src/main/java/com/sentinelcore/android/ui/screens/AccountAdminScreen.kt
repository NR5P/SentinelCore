package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MenuAnchorType
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.Account
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.EmptyState
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch

@Composable
fun AccountAdminScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
    onAddAccount: () -> Unit,
) {
    var isLoading by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var selectedAccount by remember { mutableStateOf<Account?>(null) }
    val scope = rememberCoroutineScope()

    suspend fun load() {
        isLoading = true
        try {
            model.refreshAccounts()
            errorMessage = null
        } catch (error: Exception) {
            errorMessage = error.message ?: "Could not load accounts."
        } finally {
            isLoading = false
        }
    }

    LaunchedEffect(Unit) { load() }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Accounts", onBack) {
            IconButton(onClick = onAddAccount) {
                Icon(Icons.Default.Add, contentDescription = "Add account")
            }
        }
        if (isLoading) {
            LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
        }
        errorMessage?.let {
            Text(it, color = SentinelRed, modifier = Modifier.padding(16.dp))
        }
        if (state.accounts.isEmpty() && !isLoading) {
            EmptyState(
                "No accounts",
                "Add one to give someone else access.",
                Modifier.fillMaxSize(),
            )
        } else {
            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                items(state.accounts, key = Account::id) { account ->
                    Card(Modifier.fillMaxWidth().clickable { selectedAccount = account }) {
                        Row(
                            modifier = Modifier.padding(14.dp).fillMaxWidth(),
                            verticalAlignment = androidx.compose.ui.Alignment.CenterVertically,
                        ) {
                            Column(Modifier.weight(1f)) {
                                Text(
                                    account.displayName?.let { "${account.username} ($it)" } ?: account.username,
                                    style = MaterialTheme.typography.titleSmall,
                                )
                                Text(
                                    account.role,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            IconButton(onClick = {
                                scope.launch {
                                    try {
                                        model.deleteAccount(account)
                                    } catch (error: Exception) {
                                        errorMessage = error.message ?: "Could not delete account."
                                    }
                                }
                            }) {
                                Icon(
                                    Icons.Default.Delete,
                                    contentDescription = "Delete ${account.username}",
                                    tint = SentinelRed,
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    selectedAccount?.let { account ->
        AccountActionsDialog(
            account = account,
            model = model,
            onDismiss = { selectedAccount = null },
            onError = { errorMessage = it },
        )
    }
}

@Composable
private fun AccountActionsDialog(
    account: Account,
    model: SentinelViewModel,
    onDismiss: () -> Unit,
    onError: (String) -> Unit,
) {
    var isWorking by remember { mutableStateOf(false) }
    var showResetPassword by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val otherRole = if (account.role == "Admin") "Viewer" else "Admin"

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(account.username) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(account.role, color = MaterialTheme.colorScheme.onSurfaceVariant)
                OutlinedButton(
                    onClick = {
                        scope.launch {
                            isWorking = true
                            try {
                                model.updateAccountRole(account, otherRole)
                                onDismiss()
                            } catch (error: Exception) {
                                onError(error.message ?: "Could not change the role.")
                            } finally {
                                isWorking = false
                            }
                        }
                    },
                    enabled = !isWorking,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    if (isWorking) CircularProgressIndicator(Modifier.padding(2.dp))
                    else Text("Make $otherRole")
                }
                OutlinedButton(
                    onClick = { showResetPassword = true },
                    enabled = !isWorking,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Text("Reset Password")
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text("Close") }
        },
    )

    if (showResetPassword) {
        ResetPasswordDialog(
            account = account,
            model = model,
            onDismiss = { showResetPassword = false; onDismiss() },
            onError = onError,
        )
    }
}

@Composable
private fun ResetPasswordDialog(
    account: Account,
    model: SentinelViewModel,
    onDismiss: () -> Unit,
    onError: (String) -> Unit,
) {
    var newPassword by remember { mutableStateOf("") }
    var isSaving by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Reset Password") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("Choose a new password for \"${account.username}\".")
                OutlinedTextField(
                    value = newPassword,
                    onValueChange = { newPassword = it },
                    label = { Text("New password (min. 8 characters)") },
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                errorMessage?.let { Text(it, color = SentinelRed) }
            }
        },
        confirmButton = {
            TextButton(
                enabled = newPassword.length >= 8 && !isSaving,
                onClick = {
                    scope.launch {
                        isSaving = true
                        errorMessage = null
                        try {
                            model.resetAccountPassword(account, newPassword)
                            onDismiss()
                        } catch (error: Exception) {
                            errorMessage = error.message ?: "Could not reset the password."
                        } finally {
                            isSaving = false
                        }
                    }
                },
            ) {
                Text(if (isSaving) "Saving…" else "Reset")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("Cancel") }
        },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AccountEditScreen(
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var displayName by remember { mutableStateOf("") }
    var role by remember { mutableStateOf("Viewer") }
    var roleMenuExpanded by remember { mutableStateOf(false) }
    var isSaving by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    fun save() {
        scope.launch {
            isSaving = true
            errorMessage = null
            try {
                model.createAccount(username.trim(), password, role, displayName.trim().ifBlank { null })
                onBack()
            } catch (error: Exception) {
                errorMessage = error.message ?: "Could not create this account."
            } finally {
                isSaving = false
            }
        }
    }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Add Account", onBack) {
            if (isSaving) {
                CircularProgressIndicator(Modifier.padding(8.dp))
            } else {
                TextButton(onClick = ::save, enabled = username.isNotBlank() && password.length >= 8) {
                    Text("Save")
                }
            }
        }
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            item {
                Card(Modifier.fillMaxWidth()) {
                    Column(
                        modifier = Modifier.padding(14.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp),
                    ) {
                        OutlinedTextField(
                            value = username,
                            onValueChange = { username = it },
                            label = { Text("Username") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = password,
                            onValueChange = { password = it },
                            label = { Text("Password (min. 8 characters)") },
                            visualTransformation = PasswordVisualTransformation(),
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = displayName,
                            onValueChange = { displayName = it },
                            label = { Text("Display name (optional)") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        ExposedDropdownMenuBox(
                            expanded = roleMenuExpanded,
                            onExpandedChange = { roleMenuExpanded = it },
                        ) {
                            OutlinedTextField(
                                value = role,
                                onValueChange = {},
                                readOnly = true,
                                label = { Text("Role") },
                                trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = roleMenuExpanded) },
                                modifier = Modifier.fillMaxWidth().menuAnchor(MenuAnchorType.PrimaryEditable, true),
                            )
                            ExposedDropdownMenu(
                                expanded = roleMenuExpanded,
                                onDismissRequest = { roleMenuExpanded = false },
                            ) {
                                listOf("Viewer", "Admin").forEach { option ->
                                    DropdownMenuItem(
                                        text = { Text(option) },
                                        onClick = { role = option; roleMenuExpanded = false },
                                    )
                                }
                            }
                        }
                    }
                }
            }
            errorMessage?.let { message ->
                item { Text(message, color = SentinelRed) }
            }
        }
    }
}
